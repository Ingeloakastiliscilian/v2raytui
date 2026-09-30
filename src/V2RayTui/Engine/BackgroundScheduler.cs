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
    private volatile bool _aliveFirstOnce;
    private volatile bool _restartPending;
    private CancellationTokenSource? _cycleCts;
    private Task? _retryLoop;
    private TestJob? _retryJob;

    public DateTime? NextRun { get; private set; }
    public DateTime? LastRun { get; private set; }
    public TestJob? CurrentJob { get; private set; }
    public string LastResult { get; private set; } = "";

    /// <summary>What the running cycle is doing now ("" when idle).</summary>
    public string Stage { get; private set; } = "";

    private void SetStage(string stage)
    {
        Stage = stage;
        Changed?.Invoke();
    }
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
        NetworkMonitor.Instance.Changed += OnNetworkChanged;
        NetworkMonitor.Instance.Restored += OnNetworkRestored;
        NetworkMonitor.Instance.Start();
        Reschedule();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _retryLoop = Task.Run(() => RetryLoopAsync(_cts.Token));
        if (S.BackgroundEnabled)
        {
            // Starts working right away rather than after the first interval.
            RunNow();
        }
    }

    public async Task StopAsync()
    {
        AppHost.SubscriptionsUpdated -= OnSubscriptionsUpdated;
        TestService.Instance.JobChanged -= OnJobChanged;
        NetworkMonitor.Instance.Changed -= OnNetworkChanged;
        NetworkMonitor.Instance.Restored -= OnNetworkRestored;
        NetworkMonitor.Instance.Stop();
        _cts?.Cancel();
        CurrentJob?.Cts.Cancel();
        _retryJob?.Cts.Cancel();
        foreach (var t in new[] { _loop, _retryLoop })
        {
            if (t == null)
            {
                continue;
            }
            try
            {
                // A subscription download cannot be cancelled: do not hold the exit for it.
                await t.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // ignored
            }
        }
        _loop = null;
        _retryLoop = null;
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

    /// <summary>
    /// Another network: its DNS for the core (if enabled), forget failures seen on the old one, and start the
    /// cycle over — Alive members first, so the group reflects the new network as soon as possible.
    /// </summary>
    private void OnNetworkChanged(NetworkInfo old, NetworkInfo now)
    {
        LogBus.Notice("[net] " + Loc.T($"network changed: {now}", $"сеть сменилась: {now}"));
        AliveFailures.ResetAll();
        _ = Task.Run(async () =>
        {
            try
            {
                await ProxyController.Instance.OnNetworkChangedAsync();
            }
            catch (Exception ex)
            {
                LogBus.Write($"[net] {ex.Message}");
            }
            if (S.BackgroundEnabled)
            {
                RestartCycle(Loc.T("network changed", "смена сети"));
            }
        });
    }

    private static void OnNetworkRestored(NetworkInfo now) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await ProxyController.Instance.EnsureNetworkDnsAsync(now);
            }
            catch (Exception ex)
            {
                LogBus.Write($"[net] {ex.Message}");
            }
        });

    /// <summary>Stops the running cycle (if any) and starts a new one, Alive members first.</summary>
    public void RestartCycle(string reason)
    {
        _aliveFirstOnce = true;
        // Test right away; subscriptions are updated by the next regular cycle.
        _skipSubUpdateOnce = true;
        if (IsRunning)
        {
            LogBus.Write("[bg] " + Loc.T($"restarting the cycle: {reason}", $"перезапуск цикла: {reason}"));
            _restartPending = true;
            _cycleCts?.Cancel();
        }
        else
        {
            RunNow();
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
            catch (OperationCanceledException)
            {
                // the cycle was restarted
            }
            catch (Exception ex)
            {
                Logging.SaveLog("BackgroundScheduler", ex);
                LogBus.Write($"[bg] {ex.Message}");
            }
            // Requests that arrived while the cycle was running are covered by it — except a restart.
            _runRequested = _restartPending;
            _restartPending = false;
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
        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cycleCts = cycleCts;
        ct = cycleCts.Token;
        var aliveFirst = _aliveFirstOnce;
        _aliveFirstOnce = false;
        try
        {
            Changed?.Invoke();
            if (!CoreUpdater.MainCores.Any(CoreUpdater.IsInstalled))
            {
                // Without a core every server would look dead: skip instead of recording false failures.
                LastResult = Loc.T("no core installed (F8 / core update)", "ядра не установлены (F8 / core update)");
                LogBus.Notice("[bg] " + LastResult);
                return;
            }
            var aliveOn = S.AliveEnabled;
            var aliveId = aliveOn ? await AliveGroup.EnsureAsync() : null;
            // The Alive group is a result, never a test source.
            var subId = S.BackgroundSubId == aliveId ? "" : S.BackgroundSubId;

            var skipUpdate = _skipSubUpdateOnce;
            _skipSubUpdateOnce = false;
            if ((S.UpdateSubsBeforeTest || aliveOn) && !skipUpdate)
            {
                LogBus.Write("[bg] " + Loc.T("updating subscriptions", "обновление подписок"));
                SetStage(Loc.T("updating subscriptions", "обновление подписок"));
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
                session = await AliveSession.StartAsync(items, toTest, ct);
                if (toTest.Count < items.Count)
                {
                    LogBus.Write("[bg] " + Loc.T($"{items.Count} servers, {toTest.Count} distinct", $"серверов {items.Count}, различных {toTest.Count}"));
                }
                items = toTest;
            }
            SetStage(Loc.T("testing", "проверка серверов"));
            TestJob Start(string t, IReadOnlyList<ProfileItem> list) => aliveOn
                ? TestService.Instance.Start(t, TestMode.PingThenSpeed, list, background: true, speedTopN: 0,
                    onItemFinished: session!.OnItemFinished, speedSeconds: S.BackgroundSpeedTestSeconds)
                : TestService.Instance.Start(t, S.BackgroundMode, list, background: true, speedSeconds: S.BackgroundSpeedTestSeconds);

            // After a network change the members of the group go first (their own job), then everything else.
            var first = items;
            var rest = new List<ProfileItem>();
            if (aliveFirst && session != null)
            {
                var members = session.MemberKeys;
                first = items.Where(p => members.Contains(AliveGroup.Key(p))).ToList();
                rest = items.Where(p => !first.Contains(p)).ToList();
                if (first.Count == 0)
                {
                    (first, rest) = (rest, []);
                }
                else
                {
                    LogBus.Write("[bg] " + Loc.T($"{S.AliveName} members first: {first.Count}", $"сначала участники {S.AliveName}: {first.Count}"));
                }
            }

            CurrentJob = Start(rest.Count > 0 ? $"{title}: {S.AliveName}" : title, first);
            Changed?.Invoke();
            var firstJob = CurrentJob;
            using (ct.Register(() => CurrentJob?.Cts.Cancel()))
            {
                await firstJob.Completion;
                if (rest.Count > 0 && !firstJob.Cancelled)
                {
                    CurrentJob = Start(title, rest);
                    Changed?.Invoke();
                    await CurrentJob.Completion;
                    // One result set for the reconciliation and auto switching.
                    foreach (var p in rest)
                    {
                        if (CurrentJob.Delays.TryGetValue(p.IndexId, out var d))
                        {
                            firstJob.Delays[p.IndexId] = d;
                        }
                        if (CurrentJob.Speeds.TryGetValue(p.IndexId, out var sp))
                        {
                            firstJob.Speeds[p.IndexId] = sp;
                        }
                        if (CurrentJob.IpInfos.TryGetValue(p.IndexId, out var ip))
                        {
                            firstJob.IpInfos[p.IndexId] = ip;
                        }
                    }
                    if (CurrentJob.Cancelled)
                    {
                        firstJob.Cts.Cancel();
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            if (session != null)
            {
                await session.DrainAsync();
            }
            var summary = CurrentJob.ProgressText;
            CurrentJob = firstJob;
            LastResult = summary;

            if (!CurrentJob.Cancelled)
            {
                if (aliveOn)
                {
                    SetStage(Loc.T("rebuilding Alive", "пересборка Alive"));
                    var r = await AliveGroup.SyncAsync(CurrentJob, items);
                    LastResult += $" │ {S.AliveName}: {r.Total}";
                }
                await AutoSwitchAsync(CurrentJob, aliveId);
            }
        }
        finally
        {
            _cycleCts = null;
            Stage = "";
            _runGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Re-checks Alive members that failed, each after the retry interval (≥ 1 min) — between and during
    /// cycles, with its own speed slot. A member that passes is forgiven; one that fails again moves towards
    /// removal (<see cref="TuiSettings.AliveDropAfterFailures"/>). A healthy member is tested along as a
    /// control: when it fails too, our own network is the problem and nothing is counted.
    /// </summary>
    private async Task RetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                if (S.AliveEnabled && AliveGroup.CurrentId is { } groupId && AliveFailures.DueKeys() is { Count: > 0 } due)
                {
                    await RecheckFailedAsync(groupId, due.ToHashSet(), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logging.SaveLog("BackgroundScheduler.Retry", ex);
                LogBus.Write($"[alive] {ex.Message}");
            }
        }
    }

    private async Task RecheckFailedAsync(string groupId, HashSet<string> due, CancellationToken ct)
    {
        if (!CoreUpdater.MainCores.Any(CoreUpdater.IsInstalled) || !NetworkMonitor.Instance.Current.IsUp && OperatingSystem.IsLinux())
        {
            return;
        }
        var copies = await AppManager.Instance.ProfileItems(groupId) ?? [];
        var byKey = copies.GroupBy(AliveGroup.Key).ToDictionary(g => g.Key, g => g.First());
        var targets = due.Where(byKey.ContainsKey).Select(k => (Key: k, Copy: byKey[k])).ToList();
        // Members no longer in the group need no re-check.
        AliveFailures.Retain(byKey.Keys.ToHashSet());
        if (targets.Count == 0)
        {
            return;
        }
        var exs = (await ProfileExManager.Instance.GetProfileExs()).GroupBy(e => e.IndexId).ToDictionary(g => g.Key, g => g.First());
        var control = byKey
            .Where(kv => AliveFailures.Count(kv.Key) == 0)
            .Select(kv => kv.Value)
            .OrderByDescending(c => exs.GetValueOrDefault(c.IndexId)?.Speed ?? 0)
            .FirstOrDefault();

        var items = targets.Select(t => t.Copy).ToList();
        if (control != null)
        {
            items.Add(control);
        }
        LogBus.Write("[alive] " + Loc.T($"re-checking {targets.Count} failed", $"повторная проверка не прошедших: {targets.Count}"));
        var job = TestService.Instance.Start($"{S.AliveName}: " + Loc.T("re-check", "повтор"), TestMode.PingThenSpeed, items, background: true,
            speedTopN: 0, speedSeconds: S.BackgroundSpeedTestSeconds, ownSpeedLimiter: true);
        _retryJob = job;
        using (ct.Register(() => job.Cts.Cancel()))
        {
            await job.Completion;
        }
        _retryJob = null;
        if (job.Cancelled)
        {
            return;
        }

        bool Passed(ProfileItem p) => AliveGroup.Qualifies(job.Delays.GetValueOrDefault(p.IndexId), job.Speeds.GetValueOrDefault(p.IndexId));
        var networkOk = control == null ? targets.Any(t => Passed(t.Copy)) || NetworkMonitor.Instance.Current.IsUp : Passed(control);
        var changed = false;
        foreach (var (key, copy) in targets)
        {
            if (Passed(copy))
            {
                AliveFailures.Clear(key);
                LogBus.Write("[alive] " + Loc.T($"✓ {copy.Remarks} passes again", $"✓ {copy.Remarks} снова проходит"));
                changed = true;
            }
            else if (!networkOk)
            {
                AliveFailures.Postpone(key);
            }
            else
            {
                changed |= await AliveGroup.FailAsync(key, copy);
            }
        }
        if (!networkOk)
        {
            LogBus.Write("[alive] " + Loc.T("the control server failed too — not counted (network?)", "контрольный сервер тоже не прошёл — не засчитано (сеть?)"));
        }
        await AliveGroup.ResortAsync();
        await ProfileExManager.Instance.SaveTo();
        if (changed)
        {
            ProxyController.Instance.NotifyServersChanged();
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
