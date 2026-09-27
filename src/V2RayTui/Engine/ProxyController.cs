namespace V2RayTui.Engine;

/// <summary>
/// Owns the main core process and the knobs of v2rayN's status bar: active server, system proxy,
/// routing, TUN. Mirrors MainWindowViewModel.Reload / StatusBarViewModel without ReactiveUI.
/// </summary>
public sealed class ProxyController
{
    public static ProxyController Instance { get; } = new();

    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private volatile bool _reloadPending;
    private string? _runningProfileJson;

    /// <summary>Raised (from any thread) when running state / summary / availability changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised (from any thread) after subscriptions changed the server list.</summary>
    public event Action? ServersChanged;

    public bool CoreRunning { get; private set; }
    public string RunningSummary { get; private set; } = "-";
    public string AvailabilityText { get; private set; } = "";

    private static Config Config => AppHost.Config;

    public async Task ReloadAsync()
    {
        if (!await _reloadGate.WaitAsync(0))
        {
            _reloadPending = true;
            return;
        }
        _userStopped = false;

        try
        {
            var profile = await ConfigHandler.GetDefaultServer(Config);
            if (profile == null)
            {
                LogBus.Notice(ResUI.CheckServerSettings);
                return;
            }

            var all = await CoreConfigContextBuilder.BuildAll(Config, profile);
            if (NoticeManager.Instance.NotifyValidatorResult(all.CombinedValidatorResult) && !all.Success)
            {
                return;
            }

            AvailabilityText = "";
            await CoreManager.Instance.LoadCore(all.MainResult.Context, all.PreSocksResult?.Context);
            CoreRunning = await WaitForLocalPortAsync();
            RunningSummary = profile.GetSummary();
            _runningProfileJson = JsonUtils.Serialize(profile);
            if (!CoreRunning)
            {
                AvailabilityText = ResUI.FailedToRunCore;
                await SysProxyHandler.UpdateSysProxy(Config, true);
                LogBus.Notice($"{ResUI.FailedToRunCore}: {RunningSummary}");
                StateChanged?.Invoke();
                return;
            }
            await SysProxyHandler.UpdateSysProxy(Config, false);
            StateChanged?.Invoke();

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await CheckAvailabilityAsync();
            });
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ProxyController.Reload", ex);
            LogBus.Notice(ex.Message);
        }
        finally
        {
            _reloadGate.Release();
            if (_reloadPending)
            {
                _reloadPending = false;
                await ReloadAsync();
            }
        }
    }

    /// <summary>
    /// The core is considered up once its local mixed (socks/http) inbound accepts connections
    /// and — on Linux, where it can be checked — our own core child process is still alive
    /// (otherwise another app holding the same port would look like a running core).
    /// </summary>
    private static async Task<bool> WaitForLocalPortAsync()
    {
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await client.ConnectAsync(Global.Loopback, port, cts.Token);
                await Task.Delay(300);
                return MainCoreAlive() != false;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
        return false;
    }

    /// <summary>true/false on Linux (a child process runs config.json / configPre.json), null elsewhere.</summary>
    public static bool? MainCoreAlive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        var me = Environment.ProcessId.ToString();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _))
                {
                    continue;
                }
                try
                {
                    // stat: "pid (comm) state ppid ..." — comm may contain spaces, so parse after ')'.
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (fields.Length < 2 || fields[1] != me || fields[0] == "Z")
                    {
                        continue;
                    }
                    var args = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0');
                    if (args.Any(a => a.EndsWith(Global.CoreConfigFileName) || a.EndsWith(Global.CorePreConfigFileName)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // process vanished
                }
            }
        }
        catch
        {
            return null;
        }
        return false;
    }

    private CancellationTokenSource? _watchdogCts;
    private volatile bool _userStopped;

    /// <summary>Restarts the core if it dies unexpectedly (checked every 10 s, with back-off).</summary>
    public void StartWatchdog()
    {
        if (_watchdogCts != null)
        {
            return;
        }
        _watchdogCts = new CancellationTokenSource();
        var ct = _watchdogCts.Token;
        _ = Task.Run(async () =>
        {
            var failures = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10 * Math.Min(1 << failures, 32)), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (!CoreRunning || _userStopped || _reloadGate.CurrentCount == 0 || MainCoreAlive() != false)
                {
                    failures = 0;
                    continue;
                }
                failures++;
                LogBus.Notice(Loc.T("Core process died, restarting", "Процесс ядра завершился, перезапуск"));
                CoreRunning = false;
                StateChanged?.Invoke();
                await ReloadAsync();
            }
        }, ct);
    }

    public void StopWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts = null;
    }

    /// <summary>Applies v2rayN's regional preset (routing rules + geo sources) and restarts the core.</summary>
    public async Task ApplyRegionalPresetAsync(EPresetType type)
    {
        await ConfigHandler.ApplyRegionalPreset(Config, type);
        await ConfigHandler.InitRouting(Config);
        await ConfigHandler.SaveConfig(Config);
        LogBus.Notice($"Preset: {type}");
        await CoreUpdater.UpdateGeoAsync(CoreRunning);
        await ReloadAsync();
    }

    public async Task StopAsync()
    {
        _userStopped = true;
        await _reloadGate.WaitAsync();
        try
        {
            await SysProxyHandler.UpdateSysProxy(Config, true);
            await CoreManager.Instance.CoreStop();
            CoreRunning = false;
            RunningSummary = "-";
            AvailabilityText = "";
            LogBus.Write(Loc.T("Core stopped", "Ядро остановлено"));
            StateChanged?.Invoke();
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>Makes the server active and (re)starts the core with it.</summary>
    public async Task ActivateAsync(string indexId)
    {
        if (await AppManager.Instance.GetProfileItem(indexId) is null)
        {
            LogBus.Notice(ResUI.PleaseSelectServer);
            return;
        }
        await ConfigHandler.SetDefaultServerIndex(Config, indexId);
        await ReloadAsync();
    }

    /// <summary>Real delay through the running proxy (v2rayN's "test current server").</summary>
    public async Task CheckAvailabilityAsync()
    {
        if (!CoreRunning)
        {
            return;
        }
        var item = await ConfigHandler.GetDefaultServer(Config);
        if (item == null)
        {
            return;
        }
        AvailabilityText = ResUI.Speedtesting;
        StateChanged?.Invoke();

        var result = await ConnectionHandler.RunAvailabilityCheck();
        var ip = result.GetValidIp();
        if (ip.IsNotEmpty())
        {
            ProfileExManager.Instance.SetTestIpInfo(item.IndexId, ip);
        }
        if (result.Time > 0)
        {
            ProfileExManager.Instance.SetTestDelay(item.IndexId, result.Time);
        }
        TestService.Instance.Publish(new TestUpdate(item.IndexId,
            Delay: result.Time > 0 ? result.Time : null,
            IpInfo: ip.IsNotEmpty() ? ip : null));

        AvailabilityText = string.Format(ResUI.TestMeOutput, result.Time, result.Ip);
        LogBus.Write(AvailabilityText);
        StateChanged?.Invoke();
    }

    #region system proxy / routing / tun

    public async Task SetSystemProxyAsync(ESysProxyType type)
    {
        if (Config.SystemProxyItem.SysProxyType == type)
        {
            return;
        }
        Config.SystemProxyItem.SysProxyType = type;
        await SysProxyHandler.UpdateSysProxy(Config, !CoreRunning);
        await ConfigHandler.SaveConfig(Config);
        LogBus.Write($"{ResUI.TipChangeSystemProxy} - {type}");
        StateChanged?.Invoke();
    }

    public async Task<List<RoutingItem>> GetRoutingsAsync() => await AppManager.Instance.RoutingItems() ?? [];

    public async Task SetRoutingAsync(string routingId)
    {
        var item = await AppManager.Instance.GetRoutingItem(routingId);
        if (item is null)
        {
            return;
        }
        if (await ConfigHandler.SetDefaultRouting(Config, item) == 0)
        {
            LogBus.Write($"{ResUI.TipChangeRouting}: {item.Remarks}");
            if (CoreRunning)
            {
                await ReloadAsync();
            }
            StateChanged?.Invoke();
        }
    }

    public static bool TunAllowed => Utils.IsWindows() ? Utils.IsAdministrator() : AppManager.Instance.LinuxSudoPwd.IsNotEmpty();

    /// <summary>Toggles TUN. On Linux/macOS a sudo password is required (kept in memory only).</summary>
    public async Task<bool> SetTunAsync(bool enable, string? sudoPassword)
    {
        if (enable && !Utils.IsWindows() && sudoPassword.IsNotEmpty())
        {
            AppManager.Instance.LinuxSudoPwd = sudoPassword;
        }
        if (enable && !TunAllowed)
        {
            LogBus.Notice(Utils.IsWindows()
                ? Loc.T("TUN requires running as Administrator", "Для TUN нужен запуск от администратора")
                : Loc.T("TUN requires the sudo password", "Для TUN нужен пароль sudo"));
            return false;
        }
        Config.TunModeItem.EnableTun = enable;
        await ConfigHandler.SaveConfig(Config);
        if (CoreRunning)
        {
            await ReloadAsync();
        }
        StateChanged?.Invoke();
        return true;
    }

    #endregion system proxy / routing / tun

    #region subscriptions

    public sealed record SubUpdateResult(int Updated, int Failed, List<(SubItem Sub, DateTime NextAllowed)> Skipped)
    {
        public bool Any => Updated > 0;
    }

    /// <summary>When the subscription may be requested again (rate limit, see <see cref="TuiSettings.SubUpdateMinIntervalMinutes"/>).</summary>
    public static DateTime NextAllowedUpdate(SubItem sub) =>
        sub.UpdateTime <= 0
            ? DateTime.MinValue
            : DateTimeOffset.FromUnixTimeSeconds(sub.UpdateTime).LocalDateTime.AddMinutes(AppHost.Settings.SubUpdateMinIntervalMinutes);

    /// <summary>
    /// Updates one subscription (or all when subId is empty), like v2rayN's "Update subscription", but never
    /// requests a subscription more often than allowed unless <paramref name="force"/> is set. Every attempt
    /// (successful or not) is recorded in SubItem.UpdateTime — v2rayN's own scheduler honours it as well.
    /// When the update through the proxy fails, it is retried directly within the same attempt.
    /// </summary>
    public async Task<SubUpdateResult> UpdateSubscriptionsAsync(string? subId, bool viaProxy, bool force = false)
    {
        var subs = (await AppManager.Instance.SubItems() ?? [])
            .Where(s => s.Enabled && s.Url.IsNotEmpty() && (subId.IsNullOrEmpty() || s.Id == subId))
            .ToList();
        var skipped = new List<(SubItem, DateTime)>();
        var due = new List<SubItem>();
        foreach (var s in subs)
        {
            var next = NextAllowedUpdate(s);
            if (!force && next > DateTime.Now)
            {
                skipped.Add((s, next));
            }
            else
            {
                due.Add(s);
            }
        }
        foreach (var (s, next) in skipped)
        {
            LogBus.Write($"{s.Remarks}->" + Loc.T($"skipped: updated less than {AppHost.Settings.SubUpdateMinIntervalMinutes} min ago, next at {next:HH:mm}",
                $"пропущено: обновлялась менее {AppHost.Settings.SubUpdateMinIntervalMinutes} мин назад, следующее не раньше {next:HH:mm}"));
        }

        int updated = 0, failed = 0;
        foreach (var s in due)
        {
            var ok = await UpdateOneAsync(s.Id, viaProxy && CoreRunning);
            if (!ok && viaProxy && CoreRunning)
            {
                LogBus.Write($"{s.Remarks}->" + Loc.T("retrying without proxy", "повтор без прокси"));
                ok = await UpdateOneAsync(s.Id, false);
            }
            s.UpdateTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            await ConfigHandler.AddSubItem(Config, s);
            if (ok)
            {
                updated++;
            }
            else
            {
                failed++;
            }
        }
        if (updated > 0)
        {
            await AfterSubscriptionsUpdatedAsync();
        }
        return new SubUpdateResult(updated, failed, skipped);
    }

    private async Task<bool> UpdateOneAsync(string subId, bool viaProxy)
    {
        var ok = false;
        await SubscriptionHandler.UpdateProcess(Config, subId, viaProxy, (success, msg) =>
        {
            LogBus.Write(msg);
            ok |= success;
            return Task.CompletedTask;
        });
        return ok;
    }

    /// <summary>Raises v2rayN auto-update intervals below the rate limit (its scheduler bypasses ours otherwise).</summary>
    public static async Task EnforceAutoUpdateLimitAsync()
    {
        var min = AppHost.Settings.SubUpdateMinIntervalMinutes;
        foreach (var s in await AppManager.Instance.SubItems() ?? [])
        {
            if (s.AutoUpdateInterval > 0 && s.AutoUpdateInterval < min)
            {
                LogBus.Write($"{s.Remarks}: " + Loc.T($"auto update interval {s.AutoUpdateInterval} → {min} min (rate limit)",
                    $"интервал автообновления {s.AutoUpdateInterval} → {min} мин (ограничение частоты)"));
                s.AutoUpdateInterval = min;
                await ConfigHandler.AddSubItem(Config, s);
            }
        }
    }

    /// <summary>
    /// Called after any successful subscription update (manual or v2rayN's scheduled task):
    /// restarts the core when the active profile was replaced or its parameters changed.
    /// </summary>
    public async Task AfterSubscriptionsUpdatedAsync()
    {
        ServersChanged?.Invoke();
        if (!CoreRunning)
        {
            return;
        }
        var current = await ConfigHandler.GetDefaultServer(Config);
        if (current != null && JsonUtils.Serialize(current) != _runningProfileJson)
        {
            LogBus.Write(Loc.T("Active profile changed by subscription update, restarting core",
                "Активный профиль изменён обновлением подписки, перезапуск ядра"));
            await ReloadAsync();
        }
    }

    #endregion subscriptions

    public void NotifyServersChanged() => ServersChanged?.Invoke();
}
