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
        }
        else if (sub.Remarks != S.AliveName)
        {
            sub.Remarks = S.AliveName;
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
            .Where(p => p.Subid != groupId && !p.ConfigType.IsComplexType() && p.ConfigType is not (EConfigType.Custom or EConfigType.Outbound))
            .Select(p => (P: p, Delay: job.Delays.GetValueOrDefault(p.IndexId), Speed: job.Speeds.GetValueOrDefault(p.IndexId)))
            .Where(x => x.Delay > 0 && x.Speed > 0 && x.Speed >= S.AliveMinSpeed && (S.AliveMaxDelay == 0 || x.Delay <= S.AliveMaxDelay))
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
                copy = JsonUtils.DeepCopy(q.P)!;
                copy.IndexId = string.Empty;
                copy.Subid = groupId;
                copy.IsSub = false;
                await ConfigHandler.AddServerCommon(Config, copy, true);
                added++;
            }
            order.Add((copy.IndexId, q.Delay, q.Speed));
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
