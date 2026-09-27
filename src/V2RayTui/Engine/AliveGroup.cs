namespace V2RayTui.Engine;

/// <summary>
/// The "Alive" group: copies of the servers that answered and reached the speed threshold in the last
/// background cycle. It is a subscription item without URL, so subscription updates never touch it,
/// and it is rebuilt after every cycle:
/// servers that still qualify keep their copy (same id — the active one is not disturbed),
/// new ones are added, the ones that fell below the threshold or disappeared are removed.
/// The same server found in several subscriptions is kept once.
/// </summary>
public static class AliveGroup
{
    private static TuiSettings S => AppHost.Settings;
    private static Config Config => AppHost.Config;

    /// <summary>The group id when the feature is enabled and the group exists, otherwise null.</summary>
    public static string? CurrentId => S.AliveEnabled && S.AliveSubId.IsNotEmpty() ? S.AliveSubId : null;

    /// <summary>Returns the group id, creating (or renaming) the group when needed.</summary>
    public static async Task<string> EnsureAsync()
    {
        var subs = await AppManager.Instance.SubItems() ?? [];
        var sub = subs.FirstOrDefault(x => x.Id == S.AliveSubId)
                  ?? subs.FirstOrDefault(x => x.Url.IsNullOrEmpty() && x.Remarks == S.AliveName);
        if (sub == null)
        {
            sub = new SubItem
            {
                Id = string.Empty,
                Remarks = S.AliveName,
                Url = string.Empty,
                Enabled = true,
                Memo = "v2rayn-tui: live servers above the speed threshold, rebuilt by the background cycle",
            };
            await ConfigHandler.AddSubItem(Config, sub);
            LogBus.Write($"[alive] " + Loc.T($"group \"{S.AliveName}\" created", $"создана группа «{S.AliveName}»"));
            ProxyController.Instance.NotifyServersChanged();
        }
        else if (sub.Remarks != S.AliveName)
        {
            sub.Remarks = S.AliveName;
            await ConfigHandler.AddSubItem(Config, sub);
        }
        // First in the subscription list (v2rayN orders by Sort, so the GUI shows it first too).
        var others = subs.Where(x => x.Id != sub.Id).ToList();
        if (others.Count > 0 && sub.Sort >= others.Min(x => x.Sort))
        {
            sub.Sort = others.Min(x => x.Sort) - 1;
            await ConfigHandler.AddSubItem(Config, sub);
        }
        if (S.AliveSubId != sub.Id)
        {
            S.AliveSubId = sub.Id;
            AppHost.SaveSettings();
        }
        return sub.Id;
    }

    /// <summary>
    /// Identity of a server regardless of its name and group: the share link without remarks
    /// (address, port, credentials, transport, TLS/Reality parameters…).
    /// </summary>
    public static string Key(ProfileItem p)
    {
        var c = JsonUtils.DeepCopy(p)!;
        c.Remarks = string.Empty;
        var uri = FmtHandler.GetShareUri(c);
        if (uri.IsNotEmpty())
        {
            return uri;
        }
        c.IndexId = string.Empty;
        c.Subid = string.Empty;
        c.IsSub = false;
        return JsonUtils.Serialize(c, false);
    }

    public static bool IsCandidate(ProfileItem p, string groupId) =>
        p.Subid != groupId && !p.ConfigType.IsComplexType() && p.ConfigType is not (EConfigType.Custom or EConfigType.Outbound);

    public static bool Qualifies(int delay, decimal speed) =>
        delay > 0 && speed > 0 && speed >= S.AliveMinSpeed && (S.AliveMaxDelay == 0 || delay <= S.AliveMaxDelay);

    internal static async Task<ProfileItem> AddCopyAsync(ProfileItem source, string groupId)
    {
        var copy = JsonUtils.DeepCopy(source)!;
        copy.IndexId = string.Empty;
        copy.Subid = groupId;
        copy.IsSub = false;
        await ConfigHandler.AddServerCommon(Config, copy, true);
        return copy;
    }

    /// <summary>
    /// Adds servers that passed the threshold in a manual test (the background cycle is the only one
    /// that removes). Keeps the group as fresh as the latest measurement, whoever made it.
    /// </summary>
    public static async Task AddFromTestAsync(TestJob job)
    {
        if (CurrentId is not { } groupId || job.Cancelled || job.Speeds.IsEmpty)
        {
            return;
        }
        var existing = (await AppManager.Instance.ProfileItems(groupId) ?? []).Select(Key).ToHashSet();
        var added = 0;
        foreach (var p in job.Items.Where(p => IsCandidate(p, groupId)))
        {
            var delay = job.Delays.GetValueOrDefault(p.IndexId);
            var speed = job.Speeds.GetValueOrDefault(p.IndexId);
            if (!Qualifies(delay, speed) || !existing.Add(Key(p)))
            {
                continue;
            }
            var copy = await AddCopyAsync(p, groupId);
            ProfileExManager.Instance.SetTestDelay(copy.IndexId, delay);
            ProfileExManager.Instance.SetTestSpeed(copy.IndexId, speed);
            if (job.IpInfos.TryGetValue(p.IndexId, out var ip))
            {
                ProfileExManager.Instance.SetTestIpInfo(copy.IndexId, ip);
            }
            LogBus.Write($"[alive] + {copy.Remarks} ({speed} MB/s, {Loc.T("manual test", "ручной тест")})");
            added++;
        }
        if (added > 0)
        {
            await ResortAsync();
            await ProfileExManager.Instance.SaveTo();
            ProxyController.Instance.NotifyServersChanged();
        }
    }

    /// <summary>Keeps the group sorted by speed (fastest first), then by delay.</summary>
    public static async Task ResortAsync()
    {
        if (CurrentId is not { } groupId)
        {
            return;
        }
        var ids = (await AppManager.Instance.ProfileItems(groupId) ?? []).Select(p => p.IndexId).ToHashSet();
        var exs = (await ProfileExManager.Instance.GetProfileExs()).Where(e => ids.Contains(e.IndexId)).GroupBy(e => e.IndexId).Select(g => g.First());
        var ordered = exs.OrderByDescending(e => e.Speed).ThenBy(e => e.Delay > 0 ? e.Delay : int.MaxValue).Select(e => e.IndexId).ToList();
        ordered.AddRange(ids.Except(ordered));
        for (var i = 0; i < ordered.Count; i++)
        {
            ProfileExManager.Instance.SetSort(ordered[i], (i + 1) * 10);
        }
    }

    public sealed record SyncResult(int Total, int Added, int Removed, bool Skipped);

    /// <summary>Rebuilds the group from a finished ping+speed job over <paramref name="sources"/>.</summary>
    public static async Task<SyncResult> SyncAsync(TestJob job, IReadOnlyList<ProfileItem> sources)
    {
        if (job.Cancelled)
        {
            return new SyncResult(0, 0, 0, true);
        }
        var groupId = await EnsureAsync();

        var qualified = sources
            .Where(p => IsCandidate(p, groupId))
            .Select(p => (P: p, Delay: job.Delays.GetValueOrDefault(p.IndexId), Speed: job.Speeds.GetValueOrDefault(p.IndexId)))
            .Where(x => Qualifies(x.Delay, x.Speed))
            .Select(x => (x.P, x.Delay, x.Speed, Key: Key(x.P)))
            .GroupBy(x => x.Key)
            .Select(g => g.OrderByDescending(x => x.Speed).First())
            .OrderByDescending(x => x.Speed)
            .ThenBy(x => x.Delay)
            .ToList();

        var existing = await AppManager.Instance.ProfileItems(groupId) ?? [];
        if (qualified.Count == 0)
        {
            // Everything failed at once: most likely our own network is down. Keep the last good set.
            LogBus.Notice("[alive] " + Loc.T(
                $"no server reached {S.AliveMinSpeed} MB/s — group left unchanged ({existing.Count})",
                $"ни один сервер не набрал {S.AliveMinSpeed} МБ/с — группа не изменена ({existing.Count})"));
            return new SyncResult(existing.Count, 0, 0, true);
        }

        var existingByKey = new Dictionary<string, ProfileItem>();
        var toRemove = new List<ProfileItem>();
        foreach (var e in existing)
        {
            if (!existingByKey.TryAdd(Key(e), e))
            {
                toRemove.Add(e); // duplicate copy
            }
        }
        var keys = qualified.Select(q => q.Key).ToHashSet();
        toRemove.AddRange(existingByKey.Where(kv => !keys.Contains(kv.Key)).Select(kv => kv.Value));

        var order = new List<(string Id, int Delay, decimal Speed)>();
        var added = 0;
        foreach (var q in qualified)
        {
            if (!existingByKey.TryGetValue(q.Key, out var copy))
            {
                copy = await AddCopyAsync(q.P, groupId);
                added++;
            }
            order.Add((copy.IndexId, q.Delay, q.Speed));
            if (job.IpInfos.TryGetValue(q.P.IndexId, out var ip) && ip.IsNotEmpty())
            {
                ProfileExManager.Instance.SetTestIpInfo(copy.IndexId, ip);
            }
        }

        for (var i = 0; i < order.Count; i++)
        {
            var (id, delay, speed) = order[i];
            ProfileExManager.Instance.SetTestDelay(id, delay);
            ProfileExManager.Instance.SetTestSpeed(id, speed);
            ProfileExManager.Instance.SetSort(id, (i + 1) * 10);
            // Let auto switching see the copies.
            job.Delays[id] = delay;
            job.Speeds[id] = speed;
        }

        // The active server is about to be removed.
        var activeCopy = toRemove.FirstOrDefault(r => r.IndexId == Config.IndexId);
        var keptActive = false;
        if (activeCopy != null && S.AutoSwitch == AutoSwitchMode.Off)
        {
            // Auto switching is off: never change the user's connection. Keep it until they switch.
            toRemove.Remove(activeCopy);
            keptActive = true;
            LogBus.Notice("[alive] " + Loc.T(
                $"the active server {activeCopy.Remarks} no longer qualifies — kept because auto switch is off",
                $"активный сервер {activeCopy.Remarks} больше не проходит проверку — оставлен, т.к. автопереключение выключено"));
        }
        else if (activeCopy != null)
        {
            var best = order[0].Id;
            var bestProfile = await AppManager.Instance.GetProfileItem(best);
            LogBus.Notice("[alive] " + Loc.T(
                $"active server dropped out, switching to {bestProfile?.GetSummary()} ({order[0].Speed} MB/s)",
                $"активный сервер выбыл, переключение на {bestProfile?.GetSummary()} ({order[0].Speed} МБ/с)"));
            if (ProxyController.Instance.CoreRunning)
            {
                await ProxyController.Instance.ActivateAsync(best);
            }
            else
            {
                await ConfigHandler.SetDefaultServerIndex(Config, best);
            }
        }

        if (toRemove.Count > 0)
        {
            await ConfigHandler.RemoveServers(Config, toRemove);
        }
        await ProfileExManager.Instance.SaveTo();

        LogBus.Notice("[alive] " + Loc.T(
            $"{S.AliveName}: {order.Count} servers (+{added} −{toRemove.Count}), ≥ {S.AliveMinSpeed} MB/s",
            $"{S.AliveName}: {order.Count} серверов (+{added} −{toRemove.Count}), ≥ {S.AliveMinSpeed} МБ/с"));
        ProxyController.Instance.NotifyServersChanged();
        return new SyncResult(order.Count + (keptActive ? 1 : 0), added, toRemove.Count, false);
    }
}

/// <summary>
/// Keeps the Alive group up to date while a background cycle runs: a server is added as soon as its
/// speed test passes, and dropped as soon as every copy of it (from all subscriptions) has been tested
/// without passing. The end-of-cycle <see cref="AliveGroup.SyncAsync"/> then does the final reconciliation.
/// Nothing is dropped until at least one server has passed in this cycle (our own network may be down).
/// </summary>
public sealed class AliveSession
{
    private static TuiSettings S => AppHost.Settings;

    private readonly string _groupId;
    private readonly Dictionary<string, ProfileItem> _sources;
    private readonly Dictionary<string, string> _keyOfSource = new();
    private readonly Dictionary<string, List<string>> _sourcesOfKey = new();
    private readonly Dictionary<string, int> _pending = new();
    private int _handled;
    private readonly Dictionary<string, decimal> _bestSpeed = new();
    private readonly Dictionary<string, ProfileItem> _copies = new();
    private readonly List<string> _deferredDrops = [];
    private readonly HashSet<string> _retryKeys = [];
    private bool _retrying;
    private readonly Lock _gate = new();
    private Task _chain = Task.CompletedTask;
    private bool _keptActiveNoticed;

    private AliveSession(string groupId, IEnumerable<ProfileItem> sources)
    {
        _groupId = groupId;
        _sources = sources.Where(p => AliveGroup.IsCandidate(p, groupId)).DistinctBy(p => p.IndexId).ToDictionary(p => p.IndexId);
    }

    public int Count => _copies.Count;

    /// <param name="sources">All servers of the cycle (duplicates included).</param>
    /// <param name="tested">The ones actually tested (one per distinct server).</param>
    public static async Task<AliveSession> StartAsync(IReadOnlyList<ProfileItem> sources, IReadOnlyList<ProfileItem> tested)
    {
        var groupId = await AliveGroup.EnsureAsync();
        var session = new AliveSession(groupId, sources);
        foreach (var copy in await AppManager.Instance.ProfileItems(groupId) ?? [])
        {
            session._copies.TryAdd(AliveGroup.Key(copy), copy);
        }
        foreach (var p in session._sources.Values)
        {
            var key = AliveGroup.Key(p);
            session._keyOfSource[p.IndexId] = key;
            if (!session._sourcesOfKey.TryGetValue(key, out var ids))
            {
                session._sourcesOfKey[key] = ids = [];
            }
            ids.Add(p.IndexId);
        }
        foreach (var p in tested)
        {
            if (session._keyOfSource.TryGetValue(p.IndexId, out var key))
            {
                session._pending[key] = session._pending.GetValueOrDefault(key) + 1;
            }
        }
        await session.SeedAsync();
        return session;
    }

    /// <summary>
    /// Fills the group right away from the last saved results (e.g. after a restart);
    /// the running cycle then confirms or drops each of them.
    /// </summary>
    private async Task SeedAsync()
    {
        var exs = (await ProfileExManager.Instance.GetProfileExs())
            .GroupBy(e => e.IndexId)
            .ToDictionary(g => g.Key, g => g.First());
        var added = 0;
        foreach (var (key, ids) in _sourcesOfKey)
        {
            var best = ids
                .Select(id => exs.GetValueOrDefault(id))
                .Where(e => e != null && AliveGroup.Qualifies(e.Delay, e.Speed))
                .OrderByDescending(e => e!.Speed)
                .FirstOrDefault();
            if (best == null || _copies.ContainsKey(key))
            {
                continue;
            }
            var copy = await AliveGroup.AddCopyAsync(_sources[best.IndexId], _groupId);
            _copies[key] = copy;
            ProfileExManager.Instance.SetTestDelay(copy.IndexId, best.Delay);
            ProfileExManager.Instance.SetTestSpeed(copy.IndexId, best.Speed);
            if (best.IpInfo.IsNotEmpty())
            {
                ProfileExManager.Instance.SetTestIpInfo(copy.IndexId, best.IpInfo);
            }
            added++;
        }
        if (added > 0)
        {
            await AliveGroup.ResortAsync();
            LogBus.Write("[alive] " + Loc.T($"+{added} from the last results (re-checked in this cycle)", $"+{added} по последним замерам (перепроверяются в этом цикле)"));
            ProxyController.Instance.NotifyServersChanged();
        }
    }

    /// <summary>Called from test worker threads; work is serialized.</summary>
    public void OnItemFinished(string indexId, int delay, decimal speed, string? ipInfo)
    {
        lock (_gate)
        {
            _chain = _chain.ContinueWith(_ => HandleAsync(indexId, delay, speed, ipInfo), TaskScheduler.Default).Unwrap();
        }
    }

    public Task DrainAsync()
    {
        lock (_gate)
        {
            return _chain;
        }
    }

    private async Task HandleAsync(string indexId, int delay, decimal speed, string? ipInfo)
    {
        try
        {
            if (!_keyOfSource.TryGetValue(indexId, out var key))
            {
                return;
            }
            _pending[key] = _pending.GetValueOrDefault(key) - 1;

            // The same server under other names/subscriptions gets the same result.
            foreach (var dup in _sourcesOfKey[key].Where(id => id != indexId))
            {
                ProfileExManager.Instance.SetTestDelay(dup, delay);
                if (speed > 0)
                {
                    ProfileExManager.Instance.SetTestSpeed(dup, speed);
                }
                if (ipInfo.IsNotEmpty())
                {
                    ProfileExManager.Instance.SetTestIpInfo(dup, ipInfo);
                }
                TestService.Instance.Publish(new TestUpdate(dup, Delay: delay, Speed: speed > 0 ? speed : null, DelayStatus: "", SpeedStatus: "", IpInfo: ipInfo));
            }
            // Survive a restart in the middle of a long cycle.
            if (++_handled % 5 == 0)
            {
                await ProfileExManager.Instance.SaveTo();
            }

            if (AliveGroup.Qualifies(delay, speed))
            {
                var first = _bestSpeed.Count == 0;
                if (!_bestSpeed.TryGetValue(key, out var best) || speed > best)
                {
                    _bestSpeed[key] = speed;
                }
                if (!_copies.TryGetValue(key, out var copy))
                {
                    copy = await AliveGroup.AddCopyAsync(_sources[indexId], _groupId);
                    _copies[key] = copy;
                    LogBus.Write($"[alive] + {copy.Remarks} ({speed} MB/s, {delay} ms)");
                }
                ProfileExManager.Instance.SetTestDelay(copy.IndexId, delay);
                ProfileExManager.Instance.SetTestSpeed(copy.IndexId, _bestSpeed[key]);
                if (ipInfo.IsNotEmpty())
                {
                    ProfileExManager.Instance.SetTestIpInfo(copy.IndexId, ipInfo);
                }
                TestService.Instance.Publish(new TestUpdate(copy.IndexId, Delay: delay, Speed: _bestSpeed[key], DelayStatus: "", SpeedStatus: "", IpInfo: ipInfo));
                await AliveGroup.ResortAsync();
                if (first)
                {
                    // The network works: now it is safe to drop what failed so far.
                    foreach (var k in _deferredDrops.ToList())
                    {
                        await DropAsync(k);
                    }
                    _deferredDrops.Clear();
                }
                ProxyController.Instance.NotifyServersChanged();
                return;
            }

            // Drop the server once every copy of it has been tested and none passed —
            // but a server of the group first gets a second check at the end of the cycle.
            if (_pending[key] <= 0 && !_bestSpeed.ContainsKey(key) && _copies.ContainsKey(key))
            {
                if (!_retrying)
                {
                    _retryKeys.Add(key);
                }
                else if (_bestSpeed.Count == 0)
                {
                    _deferredDrops.Add(key);
                }
                else
                {
                    await DropAsync(key);
                    ProxyController.Instance.NotifyServersChanged();
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AliveSession", ex);
            LogBus.Write($"[alive] {ex.Message}");
        }
    }

    /// <summary>At least one server passed in this cycle (so our own network works).</summary>
    public bool AnyQualified => _bestSpeed.Count > 0;

    /// <summary>
    /// Switches the session to the second-check pass and returns one server per group member that
    /// failed the first pass. Failing again removes it.
    /// </summary>
    public List<ProfileItem> BeginRetry()
    {
        _retrying = true;
        var items = new List<ProfileItem>();
        foreach (var key in _retryKeys.Where(k => !_bestSpeed.ContainsKey(k)))
        {
            var id = _sourcesOfKey[key].FirstOrDefault(i => _sources.ContainsKey(i));
            if (id != null)
            {
                _pending[key] = 1;
                items.Add(_sources[id]);
            }
        }
        return items;
    }

    private async Task DropAsync(string key)
    {
        if (!_copies.TryGetValue(key, out var copy))
        {
            return;
        }
        if (copy.IndexId == AppHost.Config.IndexId)
        {
            // The active server: never switch here when auto switching is off; otherwise the
            // end-of-cycle sync moves to the best server of the complete results.
            if (S.AutoSwitch == AutoSwitchMode.Off && !_keptActiveNoticed)
            {
                _keptActiveNoticed = true;
                LogBus.Notice("[alive] " + Loc.T(
                    $"the active server {copy.Remarks} no longer qualifies — kept because auto switch is off",
                    $"активный сервер {copy.Remarks} больше не проходит проверку — оставлен, т.к. автопереключение выключено"));
            }
            return;
        }
        var fresh = await AppManager.Instance.GetProfileItem(copy.IndexId);
        if (fresh != null)
        {
            await ConfigHandler.RemoveServers(AppHost.Config, [fresh]);
        }
        _copies.Remove(key);
        LogBus.Write($"[alive] − {copy.Remarks}");
    }
}
