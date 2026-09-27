using System.Net.Sockets;

namespace V2RayTui.Cli;

/// <summary>`v2rayn-tui` side of attaching: hands this terminal to the running instance and waits.</summary>
public static class AttachClient
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    public static async Task<int> RunAsync(string socketPath, bool justStarted)
    {
        using var socket = await ConnectAsync(socketPath, justStarted ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(15));
        if (socket is null)
        {
            Console.Error.WriteLine(L($"The background instance does not answer. Its log: {Utils.GetLogPath("daemon.log")}",
                $"Фоновый процесс не отвечает. Его журнал: {Utils.GetLogPath("daemon.log")}"));
            return 1;
        }

        // The server draws in this terminal: tell it what kind of terminal it is.
        var sb = new StringBuilder("ATTACH\n");
        foreach (var name in new[] { "TERM", "COLORTERM", "TERM_PROGRAM", "LANG", "LC_ALL", "LC_CTYPE" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v)
            {
                sb.Append(name).Append('=').Append(v).Append('\n');
            }
        }
        UnixFd.Send(socket, Encoding.UTF8.GetBytes(sb.ToString()), [0, 1, 2]);

        // Wait until the interface is closed; the terminal belongs to the server meanwhile.
        var reply = new byte[1];
        int n;
        try
        {
            n = await socket.ReceiveAsync(reply, SocketFlags.None);
        }
        catch
        {
            n = 0;
        }
        switch (n > 0 ? (char)reply[0] : 'D')
        {
            case 'S':
                Console.WriteLine(L("The interface was opened in another terminal.", "Интерфейс открыт в другом терминале."));
                break;
            case 'Q':
                break;
            default:
                Console.WriteLine(L("v2rayn-tui keeps running in the background (proxy, TUN, tests). Open again: v2rayn-tui; stop: v2rayn-tui stop",
                    "v2rayn-tui работает в фоне (прокси, TUN, тесты). Открыть снова: v2rayn-tui; остановить: v2rayn-tui stop"));
                break;
        }
        return 0;
    }

    private static async Task<Socket?> ConnectAsync(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var announced = false;
        while (sw.Elapsed < timeout)
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await s.ConnectAsync(new UnixDomainSocketEndPoint(path));
                return s;
            }
            catch
            {
                s.Dispose();
            }
            if (!announced && sw.Elapsed > TimeSpan.FromSeconds(3))
            {
                announced = true;
                Console.WriteLine(L("Waiting for the engine (first start may download cores)…", "Ожидание движка (при первом запуске может скачивать ядра)…"));
            }
            await Task.Delay(200);
        }
        return null;
    }
}
