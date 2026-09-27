using System.Globalization;

namespace V2RayTui.Engine;

/// <summary>
/// Boots v2rayN's ServiceLib without any GUI: config, database, core manager, scheduled tasks.
/// </summary>
public static class AppHost
{
    private static bool _initialized;
    private static readonly List<IDisposable> _subscriptions = [];

    public static Config Config => AppManager.Instance.Config;

    public static TuiSettings Settings { get; private set; } = new();

    /// <summary>Raised (from any thread) when a subscription update finished successfully.</summary>
    public static event Action<string>? SubscriptionsUpdated;

    /// <summary>Raised (from any thread) with live traffic statistics of the running core.</summary>
    public static event Action<ServerSpeedItem>? StatisticsUpdated;

    /// <summary>
    /// Must be called before anything touches ServiceLib paths.
    /// By default data lives in the per-user directory (~/.local/share/v2rayN) — the same place
    /// the packaged v2rayN desktop uses on Linux, so profiles are shared with it.
    /// </summary>
    /// <summary>Command-line arguments that select the data directory (passed on to a detached daemon).</summary>
    public static List<string> DataArgs { get; } = [];

    /// <param name="portable">Keep data next to the binary.</param>
    /// <param name="dataDir">
    /// Keep data in this directory instead of the per-user one. v2rayN always names its folder "v2rayN",
    /// so "DIR/v2rayN" is used unless DIR itself is named v2rayN.
    /// </param>
    public static void PrepareEnvironment(bool portable, string? dataDir = null)
    {
        if (!portable && dataDir.IsNullOrEmpty())
        {
            // Default: a directory of our own. Sharing v2rayN's (~/.local/share/v2rayN) does not work in general:
            // its cores are versioned for that GUI (e.g. v2rayN 7.20 + sing-box 1.13 vs our 7.25 engine + 1.14),
            // and the GUI and the TUI cannot run on the same data at once.
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dataDir = Path.Combine(baseDir, "v2rayn-tui");
            ImportFromGuiOnFirstRun(Path.Combine(dataDir, "v2rayN"), Path.Combine(baseDir, "v2rayN"));
        }
        if (dataDir.IsNotEmpty())
        {
            if (dataDir.StartsWith("~/"))
            {
                dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dataDir[2..]);
            }
            var full = Path.GetFullPath(dataDir).TrimEnd('/');
            var parent = Path.GetFileName(full) == "v2rayN" ? Path.GetDirectoryName(full)! : full;
            Directory.CreateDirectory(parent);
            // .NET resolves LocalApplicationData from XDG_DATA_HOME on Linux.
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", parent, EnvironmentVariableTarget.Process);
            DataArgs.AddRange(["--data", full]);
            portable = false;
        }
        else if (portable)
        {
            DataArgs.Add("--portable");
        }
        if (!portable)
        {
            Environment.SetEnvironmentVariable(Global.LocalAppData, "1", EnvironmentVariableTarget.Process);
        }
    }

    /// <summary>What the first-run import brought over (reported after start).</summary>
    public static string? ImportedFrom { get; private set; }

    /// <summary>
    /// First run: copies v2rayN's subscriptions, servers, routing and settings (not its cores — they may be too
    /// old for this engine; fresh ones are downloaded) into our own data directory.
    /// </summary>
    private static void ImportFromGuiOnFirstRun(string target, string gui)
    {
        try
        {
            var targetConfigs = Path.Combine(target, "guiConfigs");
            var guiConfigs = Path.Combine(gui, "guiConfigs");
            if (File.Exists(Path.Combine(targetConfigs, "guiNDB.db")) || !File.Exists(Path.Combine(guiConfigs, "guiNDB.db")))
            {
                return;
            }
            Directory.CreateDirectory(targetConfigs);
            foreach (var file in Directory.EnumerateFiles(guiConfigs))
            {
                var name = Path.GetFileName(file);
                if (name is "tui.lock" or "tui.pid")
                {
                    continue;
                }
                // Read with sharing: v2rayN may be running and holding its files open.
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = File.Create(Path.Combine(targetConfigs, name));
                src.CopyTo(dst);
            }
            ImportedFrom = gui;
        }
        catch
        {
            // a failed import just means starting empty
        }
    }

    public static string Version =>
        typeof(AppHost).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";

    public static string DataDir => Utils.StartupPath();

    public static async Task<bool> InitAsync(bool registerScheduledTasks)
    {
        if (_initialized)
        {
            return true;
        }

        var firstRun = !File.Exists(Utils.GetConfigPath(Global.ConfigFileName));

        _subscriptions.Add(AppEvents.SendMsgViewRequested.AsObservable().Subscribe(new ActionObserver<string>(LogBus.Write)));
        _subscriptions.Add(AppEvents.SendSnackMsgRequested.AsObservable().Subscribe(new ActionObserver<string>(LogBus.Notice)));

        if (!AppManager.Instance.InitApp())
        {
            return false;
        }

        if (firstRun)
        {
            // A terminal tool must not silently rewrite the desktop proxy settings
            // (v2rayN's default is "clear system proxy"); the user can change it in the TUI.
            Config.SystemProxyItem.SysProxyType = ESysProxyType.Unchanged;
        }
        if (firstRun && CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "ru")
        {
            Config.UiItem.CurrentLanguage = "ru";
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru");
        }
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(Config.UiItem.CurrentLanguage);

        AppManager.Instance.InitComponents();
        LogBus.EnableFile(Utils.GetLogPath());
        if (ImportedFrom != null)
        {
            LogBus.Notice(Loc.T($"First run: subscriptions, servers and settings copied from {ImportedFrom}; cores will be downloaded fresh",
                $"Первый запуск: подписки, серверы и настройки скопированы из {ImportedFrom}; ядра будут скачаны заново"));
        }
        Settings = TuiSettings.Load();
        LogBus.KeepCoreOutput = Settings.ShowCoreOutput;

        await ConfigHandler.InitBuiltinDNS(Config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(Config);
        await ConfigHandler.InitBuiltinRouting(Config);
        await ProfileExManager.Instance.Init();
        // CoreManager.Init copies cores bundled next to the binary (./bin) into the data dir and
        // throws if that folder is missing; bundling is optional for us.
        try
        {
            Directory.CreateDirectory(Utils.GetBaseDirectory("bin"));
        }
        catch
        {
            // read-only install location
        }
        try
        {
            await CoreManager.Instance.Init(Config, OnCoreMessage);
        }
        catch (Exception ex)
        {
            LogBus.Write($"core init: {ex.Message}");
        }
        await CertPemManager.Instance.Init(Config);

        await ProxyController.EnforceAutoUpdateLimitAsync();
        await ProfileFixups.NormalizeAsync();
        if (registerScheduledTasks)
        {
            // v2rayN's own scheduler: per-subscription auto update, geo files, config autosave.
            TaskManager.Instance.RegUpdateTask(Config, OnTaskMessage);
        }

        if (Config.GuiItem.EnableStatistics || Config.GuiItem.DisplayRealTimeSpeed)
        {
            await StatisticsManager.Instance.Init(Config, OnStatistics);
        }

        if (firstRun)
        {
            await ConfigHandler.SaveConfig(Config);
        }

        _initialized = true;
        return true;
    }

    /// <summary>Re-reads tui.json (daemon: SIGHUP / systemctl reload).</summary>
    public static void ReloadSettings()
    {
        Settings = TuiSettings.Load();
        LogBus.KeepCoreOutput = Settings.ShowCoreOutput;
        TestService.Instance.ApplySettings(Settings);
    }

    public static void SaveSettings()
    {
        try
        {
            Settings.Save();
            LogBus.KeepCoreOutput = Settings.ShowCoreOutput;
        }
        catch (Exception ex)
        {
            LogBus.Write($"tui.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Non-zero while test cores are being started: their "Запуск сервиса…", config path, banner and
    /// "config rejected" messages go to the log file only (the tester reports a rejected server itself).
    /// </summary>
    internal static int QuietCoreStarts;

    /// <summary>The last "Failed to start: …" of a test core (why the core rejected a config).</summary>
    internal static string? LastTestCoreError;

    private static readonly ConcurrentDictionary<string, byte> _seenDeprecations = new();

    private static Task OnCoreMessage(bool notify, string msg)
    {
        var testCore = Volatile.Read(ref QuietCoreStarts) > 0 || msg.Contains("configTest");
        if (testCore && msg.StartsWith("Failed to start"))
        {
            LastTestCoreError = msg;
        }
        // xray repeats "The feature X is deprecated" for every outbound of every test core: keep each once.
        var dep = msg.IndexOf("The feature ", StringComparison.Ordinal);
        if (dep >= 0 && msg.Contains("deprecated"))
        {
            var feature = msg[dep..Math.Min(msg.Length, msg.IndexOf(" is deprecated", dep, StringComparison.Ordinal) is var e and > 0 ? e : msg.Length)];
            if (!_seenDeprecations.TryAdd(feature, 0))
            {
                return Task.CompletedTask;
            }
        }

        if (testCore && !LogBus.KeepCoreOutput)
        {
            LogBus.WriteFileOnly("[test-core] " + msg);
        }
        else if (notify)
        {
            LogBus.Notice(msg);
        }
        else if (LogBus.KeepCoreOutput || !LooksLikeCoreOutput(msg))
        {
            LogBus.Write(msg);
        }
        else
        {
            LogBus.WriteFileOnly("[core] " + msg);
        }
        return Task.CompletedTask;
    }

    // Core stdout lines start with a date ("2026/09/27 ...") or a level tag; keep our own messages.
    private static bool LooksLikeCoreOutput(string msg) =>
        msg.Length > 4 && (char.IsDigit(msg[0]) && msg[4] == '/' || msg.StartsWith("+") || msg.StartsWith("INFO") || msg.StartsWith("WARN") || msg.StartsWith("ERROR") || msg.StartsWith("DEBUG"));

    private static Task OnTaskMessage(bool success, string msg)
    {
        LogBus.Write(msg);
        if (success)
        {
            SubscriptionsUpdated?.Invoke(msg);
        }
        return Task.CompletedTask;
    }

    private static Task OnStatistics(ServerSpeedItem item)
    {
        StatisticsUpdated?.Invoke(item);
        return Task.CompletedTask;
    }

    /// <summary>Graceful shutdown: saves state, restores system proxy, stops cores.</summary>
    public static async Task ShutdownAsync()
    {
        if (!_initialized)
        {
            return;
        }
        _initialized = false;
        try
        {
            await AppManager.Instance.AppExitAsync(false);
        }
        catch (Exception ex)
        {
            LogBus.Write($"shutdown: {ex.Message}");
        }
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }
        _subscriptions.Clear();
    }
}
