using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace V2RayTui.Engine;

/// <summary>A partial result for one server; null fields are left unchanged.</summary>
public sealed record TestUpdate(
    string IndexId,
    int? Delay = null,
    decimal? Speed = null,
    string? DelayStatus = null,
    string? SpeedStatus = null,
    string? IpInfo = null);

public sealed class TestJob
{
    private int _phaseDone;
    private int _alive;
    private int _failed;

    public required string Title { get; init; }

    /// <summary>The servers this job tests.</summary>
    public IReadOnlyList<ProfileItem> Items { get; init; } = [];
    public required TestMode Mode { get; init; }
    public bool Background { get; init; }

    /// <summary>PingThenSpeed: how many fastest alive servers to speed-test (null = settings, 0 = all).</summary>
    public int? SpeedTopN { get; init; }

    /// <summary>Extra per-job cap on simultaneous speed tests (on top of the global one), or null.</summary>
    internal AsyncLimiter? SpeedLimiter { get; init; }

    /// <summary>
    /// PingThenSpeed: called (from worker threads) when a server's result is final — right after its ping
    /// if it is dead or not speed-tested, otherwise after its speed test. Args: index id, delay, speed.
    /// </summary>
    public Action<string, int, decimal, string?>? OnItemFinished { get; init; }

    /// <summary>Exit IP / country per server ("NL 1.2.3.4", "none"), when looked up.</summary>
    public ConcurrentDictionary<string, string> IpInfos { get; } = new();

    /// <summary>Look up exit IP / country for alive servers regardless of the setting (Alive cycles).</summary>
    public bool ForceIpInfo { get; init; }

    /// <summary>Speed test duration for this job, seconds (null = v2rayN's SpeedTestTimeout).</summary>
    public int? SpeedSeconds { get; init; }

    private readonly ConcurrentDictionary<string, byte> _finished = new();

    /// <summary>Signalled when the ping phase of a PingThenSpeed job is over (or the job ended).</summary>
    public TaskCompletionSource PingPhaseDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Finish(string id)
    {
        if (!_finished.TryAdd(id, 0))
        {
            return;
        }
        try
        {
            OnItemFinished?.Invoke(id, Delays.GetValueOrDefault(id, -1), Speeds.GetValueOrDefault(id, 0), IpInfos.GetValueOrDefault(id));
        }
        catch
        {
            // a listener must not break the test
        }
    }
    public DateTime Started { get; } = DateTime.Now;
    public DateTime? Finished { get; internal set; }
    public CancellationTokenSource Cts { get; } = new();
    public Task Completion { get; internal set; } = Task.CompletedTask;

    /// <summary>Delay per tested server after the ping phase (for auto switching / reports).</summary>
    public ConcurrentDictionary<string, int> Delays { get; } = new();

    public ConcurrentDictionary<string, decimal> Speeds { get; } = new();

    public string Phase { get; internal set; } = "";
    public int PhaseTotal { get; internal set; }
    public int PhaseDone => Volatile.Read(ref _phaseDone);
    public int Alive => Volatile.Read(ref _alive);
    public int Failed => Volatile.Read(ref _failed);
    public bool IsRunning => Finished is null;
    public bool Cancelled => Cts.IsCancellationRequested;

    internal void StartPhase(string name, int total)
    {
        Phase = name;
        PhaseTotal = total;
        Interlocked.Exchange(ref _phaseDone, 0);
    }

    internal void ItemDone(bool? ok)
    {
        Interlocked.Increment(ref _phaseDone);
        if (ok == true)
        {
            Interlocked.Increment(ref _alive);
        }
        else if (ok == false)
        {
            Interlocked.Increment(ref _failed);
        }
    }

    internal void ResetCounters()
    {
        Interlocked.Exchange(ref _alive, 0);
        Interlocked.Exchange(ref _failed, 0);
    }

    public string ProgressText =>
        IsRunning
            ? $"{Phase} {PhaseDone}/{PhaseTotal}  ✓{Alive} ✗{Failed}"
            : $"{(Cancelled ? Loc.T("stopped", "остановлен") : Loc.T("done", "готово"))}  ✓{Alive} ✗{Failed}";
}

/// <summary>
/// Parallel server tester built on v2rayN's engine.
/// <para>
/// Servers are split into batches; every batch gets one core process (xray / sing-box) with a local
/// socks inbound per server. Several such processes run at once (<see cref="TuiSettings.ParallelCores"/>),
/// and the probes through them are bounded by global limits shared by all running jobs.
/// A batch whose config the core rejects is bisected until the broken server is isolated.
/// </para>
/// </summary>
public sealed class TestService
{
    public static TestService Instance { get; } = new();

    private readonly AsyncLimiter _coreSlots = new(4);
    private readonly AsyncLimiter _pingSlots = new(64);
    private readonly AsyncLimiter _speedSlots = new(3);

    // Config generation picks free local ports by scanning current listeners, so generating and
    // starting cores is serialized until each new core actually listens.
    private readonly SemaphoreSlim _coreStartGate = new(1, 1);

    private readonly Lock _jobsGate = new();
    private readonly List<TestJob> _jobs = [];

    /// <summary>Raised from worker threads for each result.</summary>
    public event Action<TestUpdate>? Updated;

    /// <summary>Raised when a job starts or finishes.</summary>
    public event Action<TestJob>? JobChanged;

    public IReadOnlyList<TestJob> Jobs
    {
        get
        {
            lock (_jobsGate)
            {
                return _jobs.ToList();
            }
        }
    }

    public IReadOnlyList<TestJob> RunningJobs => Jobs.Where(j => j.IsRunning).ToList();

    private readonly AsyncLimiter _backgroundSpeedSlots = new(1);

    public void ApplySettings(TuiSettings s)
    {
        _backgroundSpeedSlots.Limit = s.BackgroundSpeedConcurrency;
        _coreSlots.Limit = s.ParallelCores;
        _pingSlots.Limit = s.PingConcurrency;
        _speedSlots.Limit = s.SpeedConcurrency;
    }

    public TestJob Start(string title, TestMode mode, IReadOnlyList<ProfileItem> items, bool background, int? speedTopN = null,
        Action<string, int, decimal, string?>? onItemFinished = null, int? speedSeconds = null)
    {
        ApplySettings(AppHost.Settings);
        CleanupStaleTestConfigs();
        var job = new TestJob
        {
            Title = title,
            Items = items,
            Mode = mode,
            Background = background,
            SpeedTopN = speedTopN,
            // One limiter for all background jobs: a re-check running next to the main cycle must not double downloads.
            SpeedLimiter = background ? _backgroundSpeedSlots : null,
            OnItemFinished = onItemFinished,
            ForceIpInfo = onItemFinished != null,
            SpeedSeconds = speedSeconds,
        };
        lock (_jobsGate)
        {
            _jobs.RemoveAll(j => !j.IsRunning && (DateTime.Now - j.Finished!.Value).TotalMinutes > 10);
            _jobs.Add(job);
        }
        job.Completion = Task.Run(() => RunJobAsync(job, items));
        JobChanged?.Invoke(job);
        return job;
    }

    /// <summary>Cancels all jobs and waits for their core processes to be stopped (no orphans on exit).</summary>
    public async Task StopAllAsync()
    {
        var running = RunningJobs;
        StopAll();
        try
        {
            await Task.WhenAll(running.Select(j => j.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // timeout / job errors: nothing more to do
        }
        await StopAllCoresAsync();
    }

    public void StopAll(bool includeBackground = true)
    {
        foreach (var j in RunningJobs.Where(j => includeBackground || !j.Background))
        {
            j.Cts.Cancel();
        }
    }

    private async Task RunJobAsync(TestJob job, IReadOnlyList<ProfileItem> items)
    {
        var ct = job.Cts.Token;
        var testable = items
            .Where(p => !p.IndexId.IsNullOrEmpty() && p.ConfigType != EConfigType.Custom && (p.ConfigType.IsComplexType() || p.Port > 0))
            .DistinctBy(p => p.IndexId)
            .ToList();
        var pending = new ConcurrentDictionary<string, byte>(testable.Select(p => KeyValuePair.Create(p.IndexId, (byte)0)));

        LogBus.Write($"[test] {job.Title}: {testable.Count} " + Loc.T("servers", "серверов"));
        try
        {
            switch (job.Mode)
            {
                case TestMode.Tcping:
                    MarkQueued(testable, delay: true, speed: false);
                    job.StartPhase("tcping", testable.Count);
                    await RunTcpingAsync(job, testable, pending, ct);
                    break;

                case TestMode.RealPing:
                    MarkQueued(testable, delay: true, speed: false);
                    job.StartPhase("ping", testable.Count);
                    await RunCoreBatchesAsync(job, testable, (it, c) => PingItemAsync(job, it, pending, c), pending, ct);
                    break;

                case TestMode.Speed:
                    MarkQueued(testable, delay: true, speed: true);
                    job.StartPhase("speed", testable.Count);
                    await RunCoreBatchesAsync(job, testable, async (it, c) =>
                    {
                        var ms = await PingItemAsync(job, it, pending, c, countDone: false);
                        if (ms > 0)
                        {
                            await SpeedItemAsync(job, it, c);
                        }
                        else
                        {
                            Report(new TestUpdate(it.IndexId, SpeedStatus: ""));
                        }
                        pending.TryRemove(it.IndexId, out _);
                        job.ItemDone(ms > 0);
                    }, pending, ct);
                    break;

                case TestMode.PingThenSpeed:
                    MarkQueued(testable, delay: true, speed: false);
                    job.StartPhase("ping", testable.Count);
                    await RunCoreBatchesAsync(job, testable, (it, c) => PingItemAsync(job, it, pending, c), pending, ct);

                    var top = testable
                        .Where(p => job.Delays.TryGetValue(p.IndexId, out var d) && d > 0)
                        .OrderBy(p => job.Delays[p.IndexId])
                        .ToList();
                    var topN = job.SpeedTopN ?? AppHost.Settings.SpeedTopN;
                    if (topN > 0)
                    {
                        top = top.Take(topN).ToList();
                    }
                    MarkQueued(top, delay: false, speed: true);
                    foreach (var p in top)
                    {
                        pending.TryAdd(p.IndexId, 0);
                    }
                    // Dead servers and those left out of the speed phase are final already.
                    var topIds = top.Select(p => p.IndexId).ToHashSet();
                    foreach (var p in testable.Where(p => !topIds.Contains(p.IndexId)))
                    {
                        job.Finish(p.IndexId);
                    }
                    job.PingPhaseDone.TrySetResult();
                    job.StartPhase("speed", top.Count);
                    await RunCoreBatchesAsync(job, top, async (it, c) =>
                    {
                        await SpeedItemAsync(job, it, c);
                        pending.TryRemove(it.IndexId, out _);
                        job.ItemDone(null);
                        job.Finish(it.IndexId);
                    }, pending, ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogBus.Write($"[test] {job.Title}: " + ResUI.SpeedtestingStop);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TestService", ex);
            LogBus.Write($"[test] {job.Title}: {ex.Message}");
        }
        finally
        {
            // Clear "queued/testing" markers of everything that never got a result.
            foreach (var id in pending.Keys)
            {
                Report(new TestUpdate(id, DelayStatus: "", SpeedStatus: ""));
            }
            try
            {
                await ProfileExManager.Instance.SaveTo();
            }
            catch (Exception ex)
            {
                LogBus.Write($"[test] save: {ex.Message}");
            }
            job.PingPhaseDone.TrySetResult();
            job.Finished = DateTime.Now;
            LogBus.Write($"[test] {job.Title}: {job.ProgressText} ({(job.Finished.Value - job.Started).TotalSeconds:0}s)");
            JobChanged?.Invoke(job);
        }
    }

    private void MarkQueued(IEnumerable<ProfileItem> items, bool delay, bool speed)
    {
        foreach (var p in items)
        {
            Report(new TestUpdate(p.IndexId, DelayStatus: delay ? "…" : null, SpeedStatus: speed ? "…" : null));
        }
    }

    private void Report(TestUpdate u) => Updated?.Invoke(u);

    /// <summary>Publishes a result obtained outside a job (e.g. availability check of the running server).</summary>
    public void Publish(TestUpdate u) => Updated?.Invoke(u);

    #region tcping

    private async Task RunTcpingAsync(TestJob job, List<ProfileItem> items, ConcurrentDictionary<string, byte> pending, CancellationToken ct)
    {
        var tasks = items.Select(async p =>
        {
            if (p.ConfigType.IsComplexType())
            {
                Report(new TestUpdate(p.IndexId, DelayStatus: ResUI.SpeedtestingSkip));
                pending.TryRemove(p.IndexId, out _);
                job.ItemDone(null);
                return;
            }
            using (await _pingSlots.AcquireAsync(ct))
            {
                var ms = await TcpingAsync(p.Address, p.Port, ct);
                ProfileExManager.Instance.SetTestDelay(p.IndexId, ms);
                job.Delays[p.IndexId] = ms;
                Report(new TestUpdate(p.IndexId, Delay: ms, DelayStatus: ""));
                pending.TryRemove(p.IndexId, out _);
                job.ItemDone(ms > 0);
            }
        });
        await Task.WhenAll(tasks);
    }

    private static async Task<int> TcpingAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            if (!IPAddress.TryParse(host, out var ip))
            {
                var entries = await Dns.GetHostAddressesAsync(host, linked.Token);
                ip = entries.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? entries.First();
            }
            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(ip, port), linked.Token);
            return Math.Max(1, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    #endregion tcping

    #region core batches

    private async Task RunCoreBatchesAsync(TestJob job, List<ProfileItem> items,
        Func<ServerTestItem, CancellationToken, Task> perItem,
        ConcurrentDictionary<string, byte> pending, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }
        var ids = items.Select(p => p.IndexId).ToList();
        var profiles = await AppManager.Instance.GetProfileItemsByIndexIdsAsMap(ids);
        var testItems = items.Select((p, i) =>
        {
            var profile = profiles.GetValueOrDefault(p.IndexId, p);
            return new ServerTestItem
            {
                IndexId = p.IndexId,
                Address = p.Address,
                Port = p.Port,
                ConfigType = p.ConfigType,
                QueueNum = i,
                Profile = profile,
                CoreType = AppManager.Instance.GetCoreType(profile, p.ConfigType),
            };
        }).ToList();

        var batchSize = AppHost.Settings.BatchSize;
        var batches = new List<(List<ServerTestItem> Items, bool Multi)>();
        foreach (var group in testItems.GroupBy(t => t.CoreType))
        {
            if (group.Key is ECoreType.Xray or ECoreType.sing_box)
            {
                batches.AddRange(group.Chunk(batchSize).Select(c => (c.ToList(), true)));
            }
            else
            {
                // Other cores cannot share a multi-inbound config: one process per server.
                batches.AddRange(group.Select(t => (new List<ServerTestItem> { t }, false)));
            }
        }

        await Task.WhenAll(batches.Select(b => RunBatchAsync(job, b.Items, b.Multi, perItem, pending, ct)));
    }

    private static readonly ConcurrentDictionary<string, byte> _rejectedLogged = new();

    // Every test / probe core that is running, so that shutdown can stop the ones whose task did not get
    // to its own cleanup (a probe started a second before exit left an orphan that blocked the next start).
    private static readonly ConcurrentDictionary<ProcessService, byte> _liveCores = new();

    private static ProcessService? Track(ProcessService? proc)
    {
        if (proc != null)
        {
            _liveCores[proc] = 0;
        }
        return proc;
    }

    private static async Task StopCoreAsync(ProcessService? proc)
    {
        if (proc == null || !_liveCores.TryRemove(proc, out _))
        {
            return;
        }
        try
        {
            await proc.StopAsync();
            proc.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Stops every test core still running (called on shutdown after the jobs were cancelled).</summary>
    public static Task StopAllCoresAsync() => Task.WhenAll(_liveCores.Keys.ToList().Select(StopCoreAsync));
    private static DateTime _lastCleanup;

    /// <summary>
    /// Every test core leaves binConfigs/configTest*.json behind (v2rayN keeps them for debugging):
    /// thousands per day with background cycles. Files older than an hour are no longer in use.
    /// </summary>
    private static void CleanupStaleTestConfigs()
    {
        if (DateTime.Now - _lastCleanup < TimeSpan.FromMinutes(10))
        {
            return;
        }
        _lastCleanup = DateTime.Now;
        try
        {
            var cutoff = DateTime.Now.AddHours(-1);
            foreach (var f in Directory.EnumerateFiles(Utils.GetBinConfigPath(), "configTest*.json"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    private async Task RunBatchAsync(TestJob job, List<ServerTestItem> batch, bool multi,
        Func<ServerTestItem, CancellationToken, Task> perItem,
        ConcurrentDictionary<string, byte> pending, CancellationToken ct)
    {
        ProcessService? proc = null;
        var coreFailed = false;
        string? rejectReason = null;
        using (await _coreSlots.AcquireAsync(ct))
        {
            try
            {
                await _coreStartGate.WaitAsync(ct);
                Interlocked.Increment(ref AppHost.QuietCoreStarts);
                try
                {
                    AppHost.LastTestCoreError = null;
                    proc = Track(multi
                        ? await CoreManager.Instance.LoadCoreConfigSpeedtest(batch)
                        : await CoreManager.Instance.LoadCoreConfigSpeedtest(batch[0]));
                    if (proc != null)
                    {
                        await WaitForListenersAsync(proc, batch, ct);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref AppHost.QuietCoreStarts);
                    _coreStartGate.Release();
                }

                coreFailed = proc is null || proc.HasExited;
                if (coreFailed && batch.Count == 1)
                {
                    rejectReason = AppHost.LastTestCoreError;
                }
                if (!coreFailed)
                {
                    await Task.WhenAll(batch.Select(async it =>
                    {
                        if (!it.AllowTest && multi)
                        {
                            Report(new TestUpdate(it.IndexId, DelayStatus: ResUI.SpeedtestingSkip, SpeedStatus: ""));
                            pending.TryRemove(it.IndexId, out _);
                            job.ItemDone(null);
                            return;
                        }
                        try
                        {
                            await perItem(it, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Logging.SaveLog("TestService.perItem", ex);
                        }
                    }));
                }
            }
            finally
            {
                await StopCoreAsync(proc);
            }
        }

        if (!coreFailed)
        {
            return;
        }

        if (batch.Count > 1)
        {
            // One bad server makes the core reject the whole config: bisect to isolate it.
            var half = batch.Count / 2;
            await Task.WhenAll(
                RunBatchAsync(job, batch.Take(half).ToList(), multi, perItem, pending, ct),
                RunBatchAsync(job, batch.Skip(half).ToList(), multi, perItem, pending, ct));
            return;
        }

        var only = batch[0];
        // The broken server of a rejected batch: one line in the journal instead of the core's noise.
        var reason = rejectReason is { } r ? r[(r.LastIndexOf(" > ", StringComparison.Ordinal) is var i and >= 0 ? i + 3 : 0)..].Trim() : ResUI.FailedToRunCore;
        var name = (await AppManager.Instance.GetProfileItem(only.IndexId))?.Remarks ?? only.IndexId;
        if (_rejectedLogged.TryAdd(only.IndexId + "|" + reason, 0))
        {
            LogBus.Write(Loc.T($"[test] core rejects server «{name}»: {reason}", $"[тест] ядро не принимает сервер «{name}»: {reason}"));
        }
        ProfileExManager.Instance.SetTestDelay(only.IndexId, -1);
        job.Delays[only.IndexId] = -1;
        Report(new TestUpdate(only.IndexId, Delay: -1, DelayStatus: "", SpeedStatus: ResUI.FailedToRunCore));
        pending.TryRemove(only.IndexId, out _);
        job.ItemDone(false);
    }

    private static async Task WaitForListenersAsync(ProcessService proc, List<ServerTestItem> batch, CancellationToken ct)
    {
        var ports = batch.Where(t => t.AllowTest || batch.Count == 1).Select(t => t.Port).Where(p => p > 0).ToHashSet();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5) && !proc.HasExited)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                    .Select(e => e.Port)
                    .ToHashSet();
                if (ports.All(listening.Contains))
                {
                    return;
                }
            }
            catch
            {
                await Task.Delay(1000, ct);
                return;
            }
            await Task.Delay(100, ct);
        }
    }

    #endregion core batches

    #region probes

    /// <summary>
    /// Exit IP / country of one server through its own temporary core. Unlike the main proxy port, this
    /// has no routing rules, so the answer is the server's real exit (not "direct" by a bypass rule).
    /// </summary>
    public async Task<GeoInfo?> ProbeGeoAsync(ProfileItem profile, CancellationToken ct = default) =>
        (await ProbeAsync(profile, true, ct)).Geo;

    /// <summary>
    /// Real delay (and optionally exit country) of one server through its own temporary core, bypassing
    /// routing rules — what the server itself does, not what the rules send direct.
    /// </summary>
    public async Task<(int Delay, GeoInfo? Geo)> ProbeAsync(ProfileItem profile, bool withGeo, CancellationToken ct = default)
    {
        var item = new ServerTestItem
        {
            IndexId = profile.IndexId,
            Address = profile.Address,
            Port = profile.Port,
            ConfigType = profile.ConfigType,
            Profile = profile,
            CoreType = AppManager.Instance.GetCoreType(profile, profile.ConfigType),
        };
        ProcessService? proc = null;
        try
        {
            await _coreStartGate.WaitAsync(ct);
            try
            {
                proc = Track(await CoreManager.Instance.LoadCoreConfigSpeedtest(item));
                if (proc != null)
                {
                    await WaitForListenersAsync(proc, [item], ct);
                }
            }
            finally
            {
                _coreStartGate.Release();
            }
            if (proc == null || proc.HasExited)
            {
                return (-1, null);
            }
            var proxy = new WebProxy($"socks5://{Global.Loopback}:{item.Port}");
            var delay = -1;
            for (var i = 0; i < 2 && delay <= 0; i++)
            {
                delay = await ConnectionHandler.GetRealPingTime(proxy, ct);
            }
            var geo = delay > 0 && withGeo ? await GeoIp.LookupAsync(proxy, ct) : null;
            return (delay, geo);
        }
        finally
        {
            await StopCoreAsync(proc);
        }
    }

    private async Task<int> PingItemAsync(TestJob job, ServerTestItem it, ConcurrentDictionary<string, byte> pending,
        CancellationToken ct, bool countDone = true)
    {
        int ms;
        var proxy = new WebProxy($"socks5://{Global.Loopback}:{it.Port}");
        using (await _pingSlots.AcquireAsync(ct))
        {
            ms = await ConnectionHandler.GetRealPingTime(proxy, ct);
        }
        ProfileExManager.Instance.SetTestDelay(it.IndexId, ms);
        job.Delays[it.IndexId] = ms;
        Report(new TestUpdate(it.IndexId, Delay: ms, DelayStatus: ""));

        if (ms > 0 && (AppHost.Settings.QueryIpInfo || job.ForceIpInfo))
        {
            try
            {
                using (await _pingSlots.AcquireAsync(ct))
                {
                    var geo = await GeoIp.LookupAsync(proxy, ct);
                    var ipStr = geo is null ? Global.None : GeoIp.Format(geo, GeoIp.ExpectedCountry(it.Profile?.Remarks));
                    job.IpInfos[it.IndexId] = ipStr;
                    ProfileExManager.Instance.SetTestIpInfo(it.IndexId, ipStr);
                    Report(new TestUpdate(it.IndexId, IpInfo: ipStr));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // ignored
            }
        }

        if (countDone)
        {
            pending.TryRemove(it.IndexId, out _);
            job.ItemDone(ms > 0);
        }
        return ms;
    }

    private async Task SpeedItemAsync(TestJob job, ServerTestItem it, CancellationToken ct)
    {
        using (job.SpeedLimiter is { } own ? await own.AcquireAsync(ct) : null)
        using (await _speedSlots.AcquireAsync(ct))
        {
            Report(new TestUpdate(it.IndexId, SpeedStatus: ResUI.Speedtesting));
            var proxy = new WebProxy($"socks5://{Global.Loopback}:{it.Port}");
            var cfg = AppHost.Config.SpeedTestItem;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(3, job.SpeedSeconds ?? cfg.SpeedTestTimeout)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            decimal best = 0;
            var lastMsg = "";
            try
            {
                await new DownloadService().DownloadDataAsync(cfg.SpeedTestUrl, proxy, (_, msg) =>
                {
                    if (decimal.TryParse(msg, out var v) && v > 0)
                    {
                        best = Math.Max(best, v);
                        Report(new TestUpdate(it.IndexId, Speed: best, SpeedStatus: ""));
                    }
                    else if (!string.IsNullOrEmpty(msg) && msg != "0")
                    {
                        lastMsg = msg;
                    }
                    return Task.CompletedTask;
                }, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout: the speed seen so far is the result.
            }

            if (best > 0)
            {
                ProfileExManager.Instance.SetTestSpeed(it.IndexId, best);
                job.Speeds[it.IndexId] = best;
                Report(new TestUpdate(it.IndexId, Speed: best, SpeedStatus: ""));
            }
            else
            {
                var msg = lastMsg.IsNullOrEmpty() ? "0" : lastMsg;
                ProfileExManager.Instance.SetTestMessage(it.IndexId, msg);
                Report(new TestUpdate(it.IndexId, Speed: 0, SpeedStatus: msg));
            }
        }
    }

    #endregion probes
}
