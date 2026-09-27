using System.Runtime.InteropServices;

namespace V2RayTui.Cli;

/// <summary>Headless mode: keeps the proxy up, runs v2rayN's scheduled tasks and background tests.</summary>
public static class Daemon
{
    public static async Task<int> RunAsync(ArgList args)
    {
        using var lk = InstanceLock.TryAcquire("daemon", out var err);
        if (lk is null)
        {
            Console.Error.WriteLine(err);
            return 1;
        }

        StreamWriter? logFile = null;
        if (args.Get("--log") is { } logPath)
        {
            logFile = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            LogBus.LineAdded += line => logFile.WriteLine(line);
        }
        else
        {
            LogBus.EchoToConsole = true;
        }

        var stop = new TaskCompletionSource();
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stop.TrySetResult();
        });
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            stop.TrySetResult();
        });
        using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
        {
            // Reload tui.json and run a cycle now (e.g. `systemctl reload`).
            ctx.Cancel = true;
            AppHost.ReloadSettings();
            LogBus.Write("SIGHUP: tui.json reloaded");
            BackgroundScheduler.Instance.Reschedule();
            BackgroundScheduler.Instance.RunNow();
        });

        try
        {
            if (!await AppHost.InitAsync(registerScheduledTasks: true))
            {
                return 1;
            }
            LogBus.Write($"v2rayn-tui daemon, pid {Environment.ProcessId}, data: {AppHost.DataDir}");

            if (CoreUpdater.MissingComponents() is { Count: > 0 } missing)
            {
                LogBus.Write($"missing: {string.Join(", ", missing)} — " + Loc.T("downloading", "загрузка"));
                await CoreUpdater.InstallMissingAsync(viaProxy: false);
            }
            if (AppHost.Config.TunModeItem.EnableTun && !await ProxyController.TryPasswordlessSudoAsync())
            {
                LogBus.Write(Loc.T("TUN is on, but the daemon cannot ask for a sudo password: starting without TUN (README: TUN).",
                    "TUN включён, но daemon не может спросить пароль sudo: запуск без TUN (README, раздел TUN)."));
            }
            if (!args.Has("--no-core"))
            {
                await ProxyController.Instance.ReloadAsync();
            }

            var s = AppHost.Settings;
            LogBus.Write(s.BackgroundEnabled
                ? $"[bg] every {s.BackgroundIntervalMinutes} min, mode {s.BackgroundMode}, auto-switch {s.AutoSwitch}"
                : "[bg] disabled in tui.json (BackgroundEnabled=false)");
            BackgroundScheduler.Instance.Start();
            ProxyController.Instance.StartWatchdog();
            if (args.Has("--test-now"))
            {
                BackgroundScheduler.Instance.RunNow();
            }

            await stop.Task;
            LogBus.Write("shutting down…");
        }
        finally
        {
            ProxyController.Instance.StopWatchdog();
            await BackgroundScheduler.Instance.StopAsync();
            TestService.Instance.StopAll();
            await AppHost.ShutdownAsync();
            LogBus.Write("bye");
            logFile?.Dispose();
        }
        return 0;
    }

    /// <summary>Starts a detached daemon (used by the TUI's "keep running in background").</summary>
    public static int SpawnDetached()
    {
        var exe = Environment.ProcessPath!;
        var log = Utils.GetLogPath("daemon.log");
        var daemonArgs = new List<string>();
        var viaDotnet = Path.GetFileNameWithoutExtension(exe) is "dotnet";
        if (viaDotnet)
        {
            daemonArgs.Add(Environment.GetCommandLineArgs()[0]);
        }
        daemonArgs.AddRange(["daemon", "--log", log]);
        daemonArgs.AddRange(AppHost.DataArgs);

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        if (!OperatingSystem.IsWindows() && File.Exists("/usr/bin/setsid"))
        {
            // New session: survives closing the terminal.
            psi.FileName = "/usr/bin/setsid";
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(exe);
        }
        else
        {
            psi.FileName = exe;
        }
        daemonArgs.ForEach(psi.ArgumentList.Add);

        using var p = Process.Start(psi);
        Console.WriteLine(Loc.T(
            $"Running in background. Log: {log}\nOpen the TUI again with `v2rayn-tui`, stop with `v2rayn-tui stop`.",
            $"Работает в фоне. Лог: {log}\nСнова открыть TUI: `v2rayn-tui`, остановить: `v2rayn-tui stop`."));
        return 0;
    }
}
