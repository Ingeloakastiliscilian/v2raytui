namespace V2RayTui.Engine;

/// <summary>A server row as shown in the TUI. Test fields are updated from background threads.</summary>
public sealed class ServerRow
{
    public required string IndexId { get; init; }
    public EConfigType ConfigType { get; init; }
    public string Remarks { get; init; } = "";
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public string Network { get; init; } = "";
    public string StreamSecurity { get; init; } = "";
    public string Subid { get; init; } = "";
    public string SubRemarks { get; init; } = "";
    public int Sort { get; init; }

    public bool IsActive { get; set; }
    public bool Marked { get; set; }

    /// <summary>0 = not tested, -1 = failed, &gt;0 = milliseconds.</summary>
    public int Delay { get; set; }

    /// <summary>MB/s, 0 = not measured.</summary>
    public decimal Speed { get; set; }

    /// <summary>Transient state ("testing…", error text) — shown instead of the value while set.</summary>
    public string DelayStatus { get; set; } = "";

    public string SpeedStatus { get; set; } = "";

    public string IpInfo { get; set; } = "";

    public string TypeName => ConfigType.ToString();

    public string Transport =>
        string.IsNullOrEmpty(StreamSecurity) ? Network ?? "" : $"{Network}+{StreamSecurity}";

    public string Endpoint => ConfigType.IsComplexType() ? "" : $"{Address}:{Port}";

    public string Summary => ConfigType.IsComplexType() ? $"[{ConfigType}] {Remarks}" : $"[{ConfigType}] {Remarks} ({Address}:{Port})";
}

public static class ServerRepository
{
    public static async Task<List<ServerRow>> LoadAsync(string? subId, string? filter)
    {
        var config = AppHost.Config;
        var models = await AppManager.Instance.ProfileModels(subId ?? "", filter ?? "") ?? [];
        // "All servers" means the subscriptions' servers: the Alive group holds copies of them.
        if (subId.IsNullOrEmpty() && AliveGroup.CurrentId is { } aliveId)
        {
            models = models.Where(m => m.Subid != aliveId).ToList();
        }
        await ConfigHandler.SetDefaultServer(config, models);

        var exs = (await ProfileExManager.Instance.GetProfileExs())
            .GroupBy(t => t.IndexId)
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<ServerRow>(models.Count);
        foreach (var m in models)
        {
            exs.TryGetValue(m.IndexId, out var ex);
            rows.Add(new ServerRow
            {
                IndexId = m.IndexId,
                ConfigType = m.ConfigType,
                Remarks = m.Remarks ?? "",
                Address = m.Address ?? "",
                Port = m.Port,
                Network = m.Network ?? "",
                StreamSecurity = m.StreamSecurity ?? "",
                Subid = m.Subid ?? "",
                SubRemarks = m.SubRemarks ?? "",
                Sort = ex?.Sort ?? 0,
                IsActive = m.IndexId == config.IndexId,
                Delay = ex?.Delay ?? 0,
                Speed = ex?.Speed ?? 0,
                SpeedStatus = ex is { Speed: <= 0 } ? ex.Message ?? "" : "",
                IpInfo = ex?.IpInfo ?? "",
            });
        }
        return rows.OrderBy(r => r.Sort).ToList();
    }

    /// <summary>Loads the full ProfileItems for the given rows, preserving their order.</summary>
    public static async Task<List<ProfileItem>> ToProfilesAsync(IEnumerable<ServerRow> rows)
    {
        return await AppManager.Instance.GetProfileItemsOrderedByIndexIds(rows.Select(r => r.IndexId));
    }

    /// <summary>Rank used by "sort by test result": alive by speed desc/delay asc, then untested, then failed.</summary>
    public static IOrderedEnumerable<ServerRow> OrderByResult(IEnumerable<ServerRow> rows, bool bySpeed)
    {
        static int Bucket(ServerRow r) => r.Delay > 0 ? 0 : r.Delay == 0 ? 1 : 2;

        var q = rows.OrderBy(Bucket);
        return bySpeed
            ? q.ThenByDescending(r => r.Speed).ThenBy(r => r.Delay)
            : q.ThenBy(r => r.Delay).ThenByDescending(r => r.Speed);
    }

    /// <summary>Persists a new order the same way v2rayN does (ProfileEx.Sort).</summary>
    public static async Task SaveOrderAsync(IReadOnlyList<ServerRow> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            ProfileExManager.Instance.SetSort(ordered[i].IndexId, (i + 1) * 10);
        }
        await ProfileExManager.Instance.SaveTo();
    }

    /// <summary>Sorts a subscription (or all servers) by a v2rayN column, persisting the order.</summary>
    public static async Task SortByColumnAsync(string? subId, EServerColName col, bool asc)
    {
        await ConfigHandler.SortServers(AppHost.Config, subId ?? "", col.ToString(), asc);
    }
}
