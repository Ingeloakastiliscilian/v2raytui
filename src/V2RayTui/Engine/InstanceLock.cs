using System.Runtime.InteropServices;

namespace V2RayTui.Engine;

/// <summary>
/// Guarantees a single owner of the v2rayN data directory (TUI, daemon or a CLI command).
/// Uses an advisory file lock plus a pid file so other invocations can find and stop the owner.
/// Also refuses to start while cores from the same data dir are running (v2rayN GUI open) — its named
/// mutex cannot be used for that on Linux, because .NET scopes it to the login session.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private FileStream? _lockStream;

    public string Mode { get; }

    private InstanceLock(string mode) => Mode = mode;

    private static string LockPath => Utils.GetConfigPath("tui.lock");
    private static string PidPath => Utils.GetConfigPath("tui.pid");

    public static InstanceLock? TryAcquire(string mode, out string? error)
    {
        error = null;
        var inst = new InstanceLock(mode);
        if (ReadOwner() is { } running && running.Pid != Environment.ProcessId)
        {
            error = Loc.T($"Another instance is running ({running.Mode}, pid {running.Pid}).", $"Уже запущен другой экземпляр ({running.Mode}, pid {running.Pid}).");
            return null;
        }
        if (FindForeignCore() is { } foreign)
        {
            error = Loc.T(
                $"A core from this data directory is already running (pid {foreign.Pid}: {foreign.Cmd}). Is v2rayN GUI open? Close it first, or use --portable / another XDG_DATA_HOME.",
                $"Ядро из этого каталога данных уже запущено (pid {foreign.Pid}: {foreign.Cmd}). Открыт GUI v2rayN? Закройте его или используйте --portable / другой XDG_DATA_HOME.");
            return null;
        }

        try
        {
            inst._lockStream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            var owner = ReadOwner();
            error = owner is { } o
                ? Loc.T($"Another instance is running ({o.Mode}, pid {o.Pid}).", $"Уже запущен другой экземпляр ({o.Mode}, pid {o.Pid}).")
                : Loc.T("Another instance is running.", "Уже запущен другой экземпляр.");
            return null;
        }

        File.WriteAllText(PidPath, $"{Environment.ProcessId}\n{mode}\n");
        return inst;
    }

    /// <summary>Finds a running xray / sing-box / … started from this data dir's bin folder (Linux only).</summary>
    public static (int Pid, string Cmd)? FindForeignCore()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        var binDir = Path.GetFullPath(Utils.GetBinPath("")).TrimEnd('/') + "/";
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId)
                {
                    continue;
                }
                string cmdline;
                try
                {
                    cmdline = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                }
                catch
                {
                    continue;
                }
                // Skip our own speed-test cores' parents etc.: only real core binaries, incl. `sudo -S -- <core>`.
                var parts = cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(a => a.StartsWith(binDir, StringComparison.Ordinal)))
                {
                    return (pid, cmdline.Length > 120 ? cmdline[..120] + "…" : cmdline);
                }
            }
        }
        catch
        {
            // ignored
        }
        return null;
    }

    public static (int Pid, string Mode)? ReadOwner()
    {
        try
        {
            if (!File.Exists(PidPath))
            {
                return null;
            }
            var lines = File.ReadAllLines(PidPath);
            if (lines.Length >= 1 && int.TryParse(lines[0].Trim(), out var pid) && IsAlive(pid))
            {
                return (pid, lines.Length > 1 ? lines[1].Trim() : "?");
            }
        }
        catch
        {
            // ignored
        }
        return null;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Asks the current owner to shut down gracefully and waits for the lock to be released.</summary>
    public static async Task<bool> StopOwnerAsync(TimeSpan timeout)
    {
        var owner = ReadOwner();
        if (owner is null)
        {
            return true;
        }
        if (owner.Value.Pid == Environment.ProcessId)
        {
            return false;
        }

        SendTerm(owner.Value.Pid);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsAlive(owner.Value.Pid))
            {
                return true;
            }
            await Task.Delay(200);
        }
        return !IsAlive(owner.Value.Pid);
    }

    private static void SendTerm(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch
            {
                // ignored
            }
            return;
        }
        _ = sys_kill(pid, 15);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int sig);

    public void Dispose()
    {
        try
        {
            if (File.Exists(PidPath) && File.ReadAllLines(PidPath).FirstOrDefault()?.Trim() == Environment.ProcessId.ToString())
            {
                File.Delete(PidPath);
            }
        }
        catch
        {
            // ignored
        }
        _lockStream?.Dispose();
        _lockStream = null;
    }
}
