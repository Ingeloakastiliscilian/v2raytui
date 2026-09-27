namespace V2RayTui.Engine;

/// <summary>
/// Periodic background pipeline: [update subscriptions] → parallel test → [auto switch to best server].
/// Runs identically inside the TUI and in headless daemon mode.
/// </summary>
public sealed class BackgroundScheduler
{
    public static BackgroundScheduler Instance { get; } = new();

    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _runRequested;
    private volatile bool _skipSubUpdateOnce;

    public DateTime? NextRun { get; private set; }
    public DateTime? LastRun { get; private set; }
    public TestJob? CurrentJob { get; private set; }
    public string LastResult { get; private set; } = "";
    public bool IsRunning => _runGate.CurrentCount == 0;

    /// <summary>Raised (from any thread) when schedule or state changes.</summary>
    public event Action? Changed;

    private static TuiSettings S => AppHost.Settings;

    public void Start()
    {
        if (_loop != null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        AppHost.SubscriptionsUpdated += OnSubscriptionsUpdated;
        TestService.Instance.JobChanged += OnJobChanged;
        Reschedule();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        AppHost.SubscriptionsUpdated -= OnSubscriptionsUpdated;
        TestService.Instance.JobChanged -= OnJobChanged;
        _cts?.Cancel();
        CurrentJob?.Cts.Cancel();
        if (_loop != null)
        {
            try
            {
                await _loop;
            }
            catch
            {
                // ignored
            }
        }
        _loop = null;
    }

    /// <summary>Recomputes the next run from settings (call after settings changed).</summary>
    public void Reschedule()
    {
        NextRun = S.BackgroundEnabled ? (LastRun ?? DateTime.Now).AddMinutes(S.BackgroundIntervalMinutes) : null;
        if (NextRun < DateTime.Now)
        {
            NextRun = DateTime.Now.AddSeconds(5);
        }
        _wake.Release();
        Changed?.Invoke();
    }

    public void RunNow()
    {
        _runRequested = true;
        _wake.Release();
    }

    private static void OnJobChanged(TestJob job)
    {
        if (!job.IsRunning && !job.Background && S.AliveEnabled)
        {
            _ = AliveGroup.AddFromTestAsync(job);
        }
    }

    private void OnSubscriptionsUpdated(string msg)
    {
        _ = ProxyController.Instance.AfterSubscriptionsUpdatedAsync();
        if (S.TestAfterSubUpdate)
        {
            _skipSubUpdateOnce = true;
            RunNow();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = NextRun is { } next ? next - DateTime.Now : Timeout.InfiniteTimeSpan;
            if (wait != Timeout.InfiniteTimeSpan && wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }
            try
            {
                await _wake.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var due = NextRun is { } n && n <= DateTime.Now;
            if (!_runRequested && !due)
            {
                continue;
            }
            _runRequested = false;
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logging.SaveLog("BackgroundScheduler", ex);
                LogBus.Write($"[bg] {ex.Message}");
            }
            // Requests that arrived while the cycle was running are covered by it.
            _runRequested = false;
            LastRun = DateTime.Now;
            Reschedule();
        }
    }

    /// <summary>One full background cycle. Safe to call directly (e.g. `test --background` / daemon start).</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (!await _runGate.WaitAsync(0, ct))
        {
            return;
        }
        try
        {
            Changed?.Invoke();
            var aliveOn = S.AliveEnabled;
            var aliveId = aliveOn ? await AliveGroup.EnsureAsync() : null;
            // The Alive group is a result, never a test source.
            var subId = S.BackgroundSubId == aliveId ? "" : S.BackgroundSubId;

            var skipUpdate = _skipSubUpdateOnce;
            _skipSubUpdateOnce = false;
            if ((S.UpdateSubsBeforeTest || aliveOn) && !skipUpdate)
            {
                LogBus.Write("[bg] " + Loc.T("updating subscriptions", "обновление подписок"));
                // Rate-limited: subscriptions updated less than an hour ago are skipped, tests still run.
                await ProxyController.Instance.UpdateSubscriptionsAsync(subId, viaProxy: true);
            }

            var items = (await AppManager.Instance.ProfileItems(subId) ?? [])
                .Where(p => aliveId == null || p.Subid != aliveId)
                .ToList();
            // Always include the active server, so auto switching knows whether it is alive.
            if (S.AutoSwitch != AutoSwitchMode.Off && items.All(p => p.IndexId != AppHost.Config.IndexId)
                && await AppManager.Instance.GetProfileItem(AppHost.Config.IndexId) is { } active)
            {
                items.Add(active);
            }
            if (items.Count == 0)
            {
                LastResult = Loc.T("no servers", "нет серверов");
                return;
            }

            var title = Loc.T("background", "фон") + (subId.IsNullOrEmpty() ? "" : $" [{(await AppManager.Instance.GetSubItem(subId))?.Remarks}]");
            // Alive needs a speed for every server that answers the ping; the group is updated as results come.
            // Each distinct server is tested once (subscriptions often repeat servers under other names),
            // its result is copied to the duplicates.
            AliveSession? session = null;
            if (aliveOn)
            {
                var activeId = AppHost.Config.IndexId;
                var toTest = items
                    .GroupBy(AliveGroup.Key)
                    .Select(g => g.FirstOrDefault(p => p.IndexId == activeId) ?? g.First())
                    .ToList();
                session = await AliveSession.StartAsync(items, toTest);
                if (toTest.Count < items.Count)
                {
                    LogBus.Write("[bg] " + Loc.T($"{items.Count} servers, {toTest.Count} distinct", $"серверов {items.Count}, различных {toTest.Count}"));
                }
                items = toTest;
            }
            CurrentJob = aliveOn
                ? TestService.Instance.Start(title, TestMode.PingThenSpeed, items, background: true, speedTopN: 0,
                    onItemFinished: session!.OnItemFinished, speedSeconds: S.BackgroundSpeedTestSeconds)
                : TestService.Instance.Start(title, S.BackgroundMode, items, background: true, speedSeconds: S.BackgroundSpeedTestSeconds);
            Changed?.Invoke();
            using (ct.Register(() => CurrentJob?.Cts.Cancel()))
            {
                await CurrentJob.Completion;
            }
            if (session != null)
            {
                await session.DrainAsync();
                // Second check for group members that failed once (a single bad measurement must not
                // throw a working server out); only when the network worked in this cycle.
                if (!CurrentJob.Cancelled && session.AnyQualified && session.BeginRetry() is { Count: > 0 } retry)
                {
                    LogBus.Write("[alive] " + Loc.T($"re-checking {retry.Count} that failed once", $"перепроверка {retry.Count} не прошедших с первого раза"));
                    var first = CurrentJob;
                    var second = TestService.Instance.Start(title + Loc.T(": re-check", ": перепроверка"), TestMode.PingThenSpeed, retry, background: true,
                        speedTopN: 0, onItemFinished: session.OnItemFinished, speedSeconds: S.BackgroundSpeedTestSeconds);
                    using (ct.Register(() => second.Cts.Cancel()))
                    {
                        await second.Completion;
                    }
                    await session.DrainAsync();
                    // The final reconciliation sees the second result.
                    foreach (var p in retry)
                    {
                        first.Delays[p.IndexId] = second.Delays.GetValueOrDefault(p.IndexId, -1);
                        first.Speeds[p.IndexId] = second.Speeds.GetValueOrDefault(p.IndexId, 0);
                        if (second.IpInfos.TryGetValue(p.IndexId, out var ip))
                        {
                            first.IpInfos[p.IndexId] = ip;
                        }
                    }
                    if (second.Cancelled)
                    {
                        first.Cts.Cancel();
                    }
                }
            }
            LastResult = CurrentJob.ProgressText;

            if (!CurrentJob.Cancelled)
            {
                if (aliveOn)
                {
                    var r = await AliveGroup.SyncAsync(CurrentJob, items);
                    LastResult += $" │ {S.AliveName}: {r.Total}";
                }
                await AutoSwitchAsync(CurrentJob, aliveId);
            }
        }
        finally
        {
            _runGate.Release();
            Changed?.Invoke();
        }
    }

    private static async Task AutoSwitchAsync(TestJob job, string? aliveId)
    {
        if (S.AutoSwitch == AutoSwitchMode.Off || !ProxyController.Instance.CoreRunning)
        {
            return;
        }

        var config = AppHost.Config;
        var currentId = config.IndexId;
        var curDelay = job.Delays.GetValueOrDefault(currentId, 0);
        var curSpeed = job.Speeds.GetValueOrDefault(currentId, 0);

        var candidates = job.Delays.Where(kv => kv.Value > 0 && kv.Key != currentId).Select(kv => kv.Key).ToList();
        // With the Alive group on, switch within it unless another scope is chosen explicitly.
        var scope = S.AutoSwitchSubId.IsNotEmpty() ? S.AutoSwitchSubId : aliveId;
        if (scope.IsNotEmpty())
        {
            var inSub = (await AppManager.Instance.ProfileItemIndexes(scope) ?? []).ToHashSet();
            candidates = candidates.Where(inSub.Contains).ToList();
        }
        if (candidates.Count == 0)
        {
            return;
        }

        var useSpeed = !job.Speeds.IsEmpty;
        var best = useSpeed
            ? candidates.OrderByDescending(id => job.Speeds.GetValueOrDefault(id, 0)).ThenBy(id => job.Delays[id]).First()
            : candidates.OrderBy(id => job.Delays[id]).First();
        var bestDelay = job.Delays[best];
        var bestSpeed = job.Speeds.GetValueOrDefault(best, 0);

        var currentDead = curDelay < 0;
        bool doSwitch;
        if (S.AutoSwitch == AutoSwitchMode.Failover)
        {
            doSwitch = currentDead;
        }
        else
        {
            var k = S.SwitchThresholdPercent / 100m;
            doSwitch = currentDead
                || (useSpeed
                    ? bestSpeed > 0 && bestSpeed > curSpeed * (1 + k)
                    : curDelay > 0 && bestDelay < curDelay * (1 - k));
        }
        if (!doSwitch)
        {
            return;
        }

        var profile = await AppManager.Instance.GetProfileItem(best);
        if (profile is null)
        {
            return;
        }
        var what = useSpeed ? $"{bestSpeed} MB/s" : $"{bestDelay} ms";
        LogBus.Notice(Loc.T($"Auto switch → {profile.GetSummary()} ({what})", $"Автопереключение → {profile.GetSummary()} ({what})"));
        await ProxyController.Instance.ActivateAsync(best);
    }
}
