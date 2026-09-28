using System.Net.Sockets;
using System.Security.Cryptography;
using V2RayTui.Tui;

namespace V2RayTui.Engine;

/// <summary>
/// Lets `v2rayn-tui` open the interface of the already running instance (like `tmux attach`).
/// The client sends its terminal's descriptors over a Unix socket; the interface is drawn right there
/// while the proxy, TUN (sudo password in memory) and background work keep running in this process.
/// Protocol: client → "ATTACH\n" + environment lines, with fds 0/1/2 attached (SCM_RIGHTS);
/// server → one byte when the session ends: 'Q' quit, 'D' background, 'S' taken by another terminal.
/// </summary>
public sealed class AttachHost
{
    public static AttachHost Instance { get; } = new();

    private readonly Lock _gate = new();
    private Socket? _listener;
    private Session? _current;
    private Action? _onQuit;

    /// <summary>A short path (sun_path is limited to 108 bytes), one per data directory and user.</summary>
    public static string SocketPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var dir = runtime.IsNotEmpty() && Directory.Exists(runtime) ? runtime : Path.GetTempPath();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Utils.StartupPath()))))[..10].ToLowerInvariant();
            return Path.Combine(dir, $"v2rayn-tui-{UnixFd.getuid()}-{hash}.sock");
        }
    }

    public bool UiAttached
    {
        get
        {
            lock (_gate)
            {
                return _current != null;
            }
        }
    }

    public void Start(Action onQuit)
    {
        if (!OperatingSystem.IsLinux() || _listener != null)
        {
            return;
        }
        _onQuit = onQuit;
        var path = SocketPath;
        try
        {
            File.Delete(path);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _listener.Listen(4);
            new Thread(AcceptLoop) { IsBackground = true, Name = "attach-accept" }.Start();
            LogBus.WriteFileOnly($"[attach] listening on {path}");
        }
        catch (Exception ex)
        {
            LogBus.Write($"[attach] {ex.Message}");
        }
    }

    public void Stop()
    {
        Session? s;
        lock (_gate)
        {
            s = _current;
        }
        s?.End('Q');
        try
        {
            _listener?.Close();
            File.Delete(SocketPath);
        }
        catch
        {
            // ignored
        }
        _listener = null;
    }

    private void AcceptLoop()
    {
        while (_listener != null)
        {
            Socket client;
            try
            {
                client = _listener.Accept();
            }
            catch
            {
                return;
            }
            try
            {
                Handle(client);
            }
            catch (Exception ex)
            {
                LogBus.Write($"[attach] {ex.Message}");
                client.Dispose();
            }
        }
    }

    private void Handle(Socket client)
    {
        var buffer = new byte[16 * 1024];
        var n = UnixFd.Receive(client, buffer, out var fds);
        var text = Encoding.UTF8.GetString(buffer, 0, n);
        if (!text.StartsWith("ATTACH\n") || fds.Length < 2)
        {
            foreach (var fd in fds)
            {
                UnixFd.close(fd);
            }
            client.Dispose();
            return;
        }

        // Only one interface at a time: the newest terminal takes it over.
        Session? previous;
        lock (_gate)
        {
            previous = _current;
        }
        if (previous != null)
        {
            previous.End('S');
            previous.WaitDone(TimeSpan.FromSeconds(10));
        }

        // The terminal type etc. of the client's terminal (the server may have been started elsewhere).
        foreach (var line in text.Split('\n').Skip(1))
        {
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq] is "TERM" or "COLORTERM" or "TERM_PROGRAM" or "LANG" or "LC_ALL" or "LC_CTYPE")
            {
                Environment.SetEnvironmentVariable(line[..eq], line[(eq + 1)..]);
            }
        }

        var session = new Session(this, client, fds);
        lock (_gate)
        {
            _current = session;
        }
        session.Start();
    }

    private void SessionFinished(Session s, TuiExit exit, char reason)
    {
        lock (_gate)
        {
            if (_current == s)
            {
                _current = null;
            }
        }
        if (reason == 'Q' || (reason == '\0' && exit == TuiExit.Quit))
        {
            _onQuit?.Invoke();
        }
    }

    private sealed class Session(AttachHost host, Socket client, int[] fds)
    {
        private readonly ManualResetEventSlim _done = new();
        private volatile char _reason;
        private int _replied;

        public void Start()
        {
            new Thread(RunUi) { IsBackground = true, Name = "tui" }.Start();
            // The client just waits; EOF means its terminal is gone (closed window, killed): leave quietly.
            new Thread(() =>
            {
                try
                {
                    var b = new byte[1];
                    while (client.Receive(b) > 0)
                    {
                    }
                }
                catch
                {
                    // disconnected
                }
                if (!_done.IsSet)
                {
                    _reason = 'D';
                    TuiApp.ForceDetach();
                }
            }) { IsBackground = true, Name = "attach-client" }.Start();
        }

        public void End(char reason)
        {
            if (_done.IsSet)
            {
                return;
            }
            _reason = reason;
            TuiApp.ForceDetach();
        }

        public void WaitDone(TimeSpan timeout) => _done.Wait(timeout);

        private void RunUi()
        {
            var exit = TuiExit.Detach;
            var echo = LogBus.EchoToConsole;
            try
            {
                LogBus.EchoToConsole = false; // never print over the interface
                UnixFd.dup2(fds[0], 0);
                UnixFd.dup2(fds[1], 1);
                UnixFd.dup2(fds.Length > 2 ? fds[2] : fds[1], 2);
                foreach (var fd in fds)
                {
                    UnixFd.close(fd);
                }
                LogBus.WriteFileOnly("[attach] interface opened");
                if (_reason == '\0')
                {
                    // (Ended before the window existed — taken over or the terminal is already gone.)
                    // End() only sets _reason; the UI thread polls it, no cross-thread calls into Terminal.Gui.
                    exit = TuiApp.Run(() => _reason != '\0');
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AttachHost.RunUi", ex);
                LogBus.WriteFileOnly($"[attach] {ex}");
            }
            finally
            {
                // Terminal.Gui has restored the terminal; let go of it.
                UnixFd.StdioToDevNull();
                LogBus.EchoToConsole = echo;
                var reason = _reason != '\0' ? _reason : exit == TuiExit.Quit ? 'Q' : 'D';
                Reply(reason);
                _done.Set();
                LogBus.WriteFileOnly($"[attach] interface closed ({reason})");
                host.SessionFinished(this, exit, _reason);
            }
        }

        private void Reply(char reason)
        {
            if (Interlocked.Exchange(ref _replied, 1) != 0)
            {
                return;
            }
            try
            {
                client.Send([(byte)reason]);
                client.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // client already gone
            }
            client.Dispose();
        }
    }
}
