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

    /// <summary>The local port another application holds (last start failed because of it), or null.</summary>
    public int? PortConflict { get; private set; }
    public string RunningSummary { get; private set; } = "-";

    /// <summary>Name of the server the core runs with ("" when stopped).</summary>
    public string RunningRemarks { get; private set; } = "";

    /// <summary>Real delay of the last connection check, ms (0 = unknown, -1 = failed).</summary>
    public int LastDelay { get; private set; }

    /// <summary>A connection check is in progress.</summary>
    public bool Checking { get; private set; }
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

            // TUN is wanted (saved setting) but sudo is not available in this session: start without it,
            // keeping the setting, so the next start with a password brings TUN back.
            // The build uses a copy of the config: the shared file (used by v2rayN GUI too) stays as it is.
            var tunWanted = Config.TunModeItem.EnableTun;
            var foreignTun = tunWanted && ForeignTunPresent();
            var tunSuppressed = tunWanted && (!TunAllowed || foreignTun);
            if (tunSuppressed)
            {
                LogBus.Notice(foreignTun
                    ? Loc.T("TUN interface singbox_tun is already up in another application (v2rayN GUI?) — close it or turn its TUN off; starting without TUN",
                        "TUN-интерфейс singbox_tun уже поднят другим приложением (GUI v2rayN?) — закройте его или выключите там TUN; запуск без TUN")
                    : Loc.T("TUN is on but no sudo access in this session — starting without TUN",
                        "TUN включён, но в этом сеансе нет доступа sudo — запуск без TUN"));
            }
            var buildConfig = JsonUtils.DeepCopy(Config)!;
            buildConfig.TunModeItem.EnableTun = tunWanted && !tunSuppressed;
            if (AppHost.Settings.TunViaSingBox)
            {
                buildConfig.TunModeItem.EnableLegacyProtect = true;
            }
            var all = await CoreConfigContextBuilder.BuildAll(buildConfig, profile);
            _tunInterface = all.PreSocksResult?.Context.IsTunEnabled == true || all.MainResult.Context.RunCoreType == ECoreType.sing_box
                ? "singbox_tun"
                : "xray_tun";
            TunActive = tunWanted && !tunSuppressed;
            if (NoticeManager.Instance.NotifyValidatorResult(all.CombinedValidatorResult) && !all.Success)
            {
                return;
            }

            AvailabilityText = "";
            await CoreManager.Instance.LoadCore(all.MainResult.Context, all.PreSocksResult?.Context);
            CoreRunning = await WaitForLocalPortAsync();
            RunningSummary = profile.GetSummary();
            RunningRemarks = profile.Remarks ?? "";
            LastDelay = 0;
            ExitGeo = null;
            _runningProfileJson = JsonUtils.Serialize(profile);
            if (!CoreRunning)
            {
                AvailabilityText = ResUI.FailedToRunCore;
                var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
                if (await PortAnswersAsync(port))
                {
                    PortConflict = port;
                    var free = SuggestFreeBasePort();
                    AvailabilityText = Loc.T($"port {port} is used by another application", $"порт {port} занят другим приложением");
                    LogBus.Notice(Loc.T(
                        $"Local port {port} is already used by another application (v2rayN GUI?). Change it: F2 → Local proxy" + (free != null ? $" (free: {free})." : "."),
                        $"Локальный порт {port} уже занят другим приложением (GUI v2rayN?). Смените его: F2 → Локальный прокси" + (free != null ? $" (свободен {free})." : ".")));
                }
                await SysProxyHandler.UpdateSysProxy(Config, true);
                LogBus.Notice($"{ResUI.FailedToRunCore}: {RunningSummary}");
                StateChanged?.Invoke();
                return;
            }
            PortConflict = null;
            await SysProxyHandler.UpdateSysProxy(Config, false);
            if (TunActive)
            {
                await CheckTunUpAsync();
            }
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

    /// <summary>
    /// A base port whose whole range is free. v2rayN derives several ports from the base
    /// (+0…+6: socks, second/LAN socks, pac, api…; +21 and up: speed tests), so candidates step by 100.
    /// </summary>
    public static int? SuggestFreeBasePort()
    {
        var current = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var used = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        for (var basePort = current + 100; basePort < 60000; basePort += 100)
        {
            if (Enumerable.Range(basePort, 7).Append(basePort + (int)EInboundProtocol.speedtest).All(p => !used.Contains(p)))
            {
                return basePort;
            }
        }
        return null;
    }

    /// <summary>Moves the local (mixed socks+http) port and restarts the core.</summary>
    public async Task ChangeLocalPortAsync(int port)
    {
        Config.Inbound.First().LocalPort = port;
        AppManager.Instance.Reset();
        await ConfigHandler.SaveConfig(Config);
        LogBus.Notice(Loc.T($"Local port changed to {port}", $"Локальный порт изменён на {port}"));
        await ReloadAsync();
    }

    private static async Task<bool> PortAnswersAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(Global.Loopback, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
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
            RunningRemarks = "";
            LastDelay = 0;
            ExitGeo = null;
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

    /// <summary>Exit IP / country of the running proxy (last check), or null.</summary>
    public GeoInfo? ExitGeo { get; private set; }

    /// <summary>Country the active server's name claims (last check), or null.</summary>
    public string? ExpectedCountry { get; private set; }

    public bool CountryMismatch => ExitGeo != null && ExpectedCountry != null && ExitGeo.Country != ExpectedCountry;

    private int _checkGeneration;

    /// <summary>
    /// Connection check of the running proxy: real delay, then exit IP and its country, compared with the
    /// country the server name claims (flag / code / name). Runs after every (re)connect and on demand.
    /// </summary>
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
        // A newer check (server switched meanwhile) wins.
        var generation = Interlocked.Increment(ref _checkGeneration);
        ExitGeo = null;
        ExpectedCountry = GeoIp.ExpectedCountry(item.Remarks);
        AvailabilityText = ResUI.Speedtesting;
        Checking = true;
        StateChanged?.Invoke();

        var proxy = new WebProxy($"socks5://{Global.Loopback}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}");
        var delay = -1;
        for (var i = 0; i < 2 && delay <= 0; i++)
        {
            delay = await ConnectionHandler.GetRealPingTime(proxy);
            if (delay <= 0)
            {
                await Task.Delay(500);
            }
        }
        if (TunActive && TunUp == true)
        {
            // Without the proxy port: this is what every app sees when TUN works.
            SystemExitGeo = await GeoIp.LookupAsync(null);
            LogBus.Write(SystemExitGeo is { } sys
                ? Loc.T($"TUN: system traffic exits via {sys.Country} {sys.CountryName} {sys.Ip}", $"TUN: трафик системы выходит через {sys.Country} {sys.CountryName} {sys.Ip}")
                : Loc.T("TUN: system traffic has no internet access", "TUN: у трафика системы нет доступа в интернет"));
        }
        GeoInfo? geo = null;
        if (delay > 0 && AppHost.Settings.CheckCountryOnConnect)
        {
            // Through a separate core without routing rules: a bypass rule must not hide the server's exit.
            geo = await TestService.Instance.ProbeGeoAsync(item);
        }
        if (generation != Volatile.Read(ref _checkGeneration))
        {
            return;
        }
        Checking = false;
        LastDelay = delay;

        string? ipText = null;
        if (delay > 0)
        {
            ProfileExManager.Instance.SetTestDelay(item.IndexId, delay);
        }
        if (geo != null)
        {
            ipText = GeoIp.Format(geo, ExpectedCountry);
            ProfileExManager.Instance.SetTestIpInfo(item.IndexId, ipText);
        }
        ExitGeo = geo;
        TestService.Instance.Publish(new TestUpdate(item.IndexId, Delay: delay > 0 ? delay : null, IpInfo: ipText));

        var sb = new StringBuilder();
        sb.Append(delay > 0 ? $"{delay} {Loc.T("ms", "мс")}" : Loc.T("no connection", "нет соединения"));
        if (geo != null)
        {
            sb.Append($" │ {geo.Country} {geo.CountryName} {geo.Ip}");
            if (CountryMismatch)
            {
                sb.Append($" ⚠ {Loc.T("name says", "в названии")} {ExpectedCountry}");
            }
        }
        else if (delay > 0 && AppHost.Settings.CheckCountryOnConnect)
        {
            sb.Append(" │ " + Loc.T("country: unknown", "страна: не определена"));
        }
        AvailabilityText = sb.ToString();

        var summary = $"{item.Remarks}: {AvailabilityText}";
        if (CountryMismatch)
        {
            LogBus.Notice(Loc.T(
                $"⚠ {item.Remarks}: exit country {geo!.Country} ({geo.CountryName}), the name says {ExpectedCountry} ({GeoIp.CountryName(ExpectedCountry!)})",
                $"⚠ {item.Remarks}: страна выхода {geo!.Country} ({geo.CountryName}), а в названии {ExpectedCountry} ({GeoIp.CountryName(ExpectedCountry!)})"));
        }
        else
        {
            LogBus.Write(summary);
        }
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

    /// <summary>
    /// v2rayN always names its TUN interface singbox_tun (and uses route table 2022): if it exists while
    /// our TUN is not running, another app (usually v2rayN GUI) owns it and a second TUN would break routing.
    /// </summary>
    public bool ForeignTunPresent() =>
        OperatingSystem.IsLinux() && Directory.Exists("/sys/class/net/singbox_tun") && !(TunActive && CoreRunning);

    private string _tunInterface = "singbox_tun";

    /// <summary>Last verdict about TUN after a start: null = unknown / not used.</summary>
    public bool? TunUp { get; private set; }

    /// <summary>Exit of traffic that does not use the proxy port (i.e. goes through TUN), or null.</summary>
    public GeoInfo? SystemExitGeo { get; private set; }

    private async Task CheckTunUpAsync()
    {
        TunUp = null;
        SystemExitGeo = null;
        var dev = $"/sys/class/net/{_tunInterface}";
        for (var i = 0; i < 25 && !Directory.Exists(dev); i++)
        {
            await Task.Delay(200);
        }
        TunUp = !OperatingSystem.IsLinux() || Directory.Exists(dev);
        if (TunUp == true)
        {
            LogBus.Write(Loc.T($"TUN: interface {_tunInterface} is up", $"TUN: интерфейс {_tunInterface} поднят"));
        }
        else
        {
            LogBus.Notice(Loc.T($"TUN: interface {_tunInterface} did not come up — core output is in guiLogs/tui-*.log; an outdated core? (F8)",
                $"TUN: интерфейс {_tunInterface} не поднялся — вывод ядра в guiLogs/tui-*.log; устаревшее ядро? (F8)"));
        }
    }

    /// <summary>TUN is part of the running configuration.</summary>
    public bool TunActive { get; private set; }

    /// <summary>
    /// Sudo without a password already works for the TUN core (a sudoers rule): TUN can start unattended.
    /// The engine pipes LinuxSudoPwd to `sudo -S`, so a placeholder is set.
    /// </summary>
    public static async Task<bool> TryPasswordlessSudoAsync()
    {
        if (Utils.IsWindows() || TunAllowed)
        {
            return TunAllowed;
        }
        var core = CoreInfoManager.Instance.GetCoreExecFile(CoreInfoManager.Instance.GetCoreInfo(ECoreType.sing_box), out _);
        if (core.IsNullOrEmpty())
        {
            return false;
        }
        // -k: ignore cached credentials. Without it a password typed a few minutes ago (sudo keeps it
        // ~15 min) looks like a passwordless rule, and later core starts/stops fail once it expires.
        var rc = await RunSudoAsync(["-n", "-k", "-l", core], null);
        LogBus.WriteFileOnly($"[tun] passwordless sudo check for {core}: exit {rc}");
        if (rc == 0)
        {
            AppManager.Instance.LinuxSudoPwd = "nopasswd";
            LogBus.Write(Loc.T("sudo without password is allowed for the TUN core", "sudo без пароля разрешён для ядра TUN"));
            return true;
        }
        return false;
    }

    /// <summary>Checks a sudo password (`sudo -k -S -v`) and keeps it in memory for this session.</summary>
    public static async Task<bool> UseSudoPasswordAsync(string password)
    {
        var rc = await RunSudoAsync(["-k", "-S", "-p", "", "-v"], password);
        LogBus.Write(rc == 0
            ? Loc.T("sudo password accepted", "Пароль sudo принят")
            : Loc.T($"sudo password rejected (exit {rc})", $"Пароль sudo не принят (код {rc})"));
        if (rc != 0)
        {
            return false;
        }
        AppManager.Instance.LinuxSudoPwd = password;
        return true;
    }

    private static async Task<int> RunSudoAsync(IEnumerable<string> args, string? stdin)
    {
        try
        {
            var psi = new ProcessStartInfo("sudo") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi)!;
            await p.StandardInput.WriteLineAsync(stdin ?? "");
            p.StandardInput.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Toggles TUN. On Linux/macOS a sudo password is required (kept in memory only).</summary>
    public async Task<bool> SetTunAsync(bool enable, string? sudoPassword)
    {
        if (enable && !Utils.IsWindows() && sudoPassword.IsNotEmpty() && !await UseSudoPasswordAsync(sudoPassword))
        {
            LogBus.Notice(Loc.T("Wrong sudo password", "Неверный пароль sudo"));
            return false;
        }
        if (enable && !TunAllowed)
        {
            await TryPasswordlessSudoAsync();
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
