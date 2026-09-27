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
    public static void PrepareEnvironment(bool portable)
    {
        if (!portable)
        {
            Environment.SetEnvironmentVariable(Global.LocalAppData, "1", EnvironmentVariableTarget.Process);
        }
    }

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

    private static Task OnCoreMessage(bool notify, string msg)
    {
        if (notify)
        {
            LogBus.Notice(msg);
        }
        else if (LogBus.KeepCoreOutput || !LooksLikeCoreOutput(msg))
        {
            LogBus.Write(msg);
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
