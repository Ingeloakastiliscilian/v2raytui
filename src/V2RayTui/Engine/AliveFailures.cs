using System.Text.Json;

namespace V2RayTui.Engine;

/// <summary>
/// Failed checks of Alive members (by <see cref="AliveGroup.Key"/>). A member is dropped only after
/// <see cref="TuiSettings.AliveDropAfterFailures"/> failures in a row, counted at least
/// <see cref="TuiSettings.AliveRetryMinutes"/> apart; a pass clears the record. Kept in guiConfigs/alive-failures.json
/// so a restart does not forgive (or double-count) a failing server.
/// </summary>
public static class AliveFailures
{
    public sealed class Entry
    {
        public int Count { get; set; }
        public DateTime LastUtc { get; set; }
        public DateTime NextUtc { get; set; }
        public string Name { get; set; } = "";
    }

    private static readonly Lock _gate = new();
    private static Dictionary<string, Entry>? _map;

    private static TuiSettings S => AppHost.Settings;
    private static TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, S.AliveRetryMinutes));
    private static string FilePath => Utils.GetConfigPath("alive-failures.json");

    private static Dictionary<string, Entry> Map
    {
        get
        {
            if (_map != null)
            {
                return _map;
            }
            try
            {
                _map = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) : null;
            }
            catch
            {
                _map = null;
            }
            return _map ??= new Dictionary<string, Entry>();
        }
    }

    public static int Count(string key)
    {
        lock (_gate)
        {
            return Map.TryGetValue(key, out var e) ? e.Count : 0;
        }
    }

    /// <summary>
    /// Records a failed check. Failures closer than the retry interval to the previous one are the same failure
    /// (e.g. a cycle result right after a re-check). Returns the count and whether the member must go.
    /// </summary>
    public static (int Count, bool Drop) Register(string key, string name)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!Map.TryGetValue(key, out var e))
            {
                Map[key] = e = new Entry();
            }
            e.Name = name;
            if (e.Count == 0 || now - e.LastUtc >= Interval)
            {
                e.Count++;
                e.LastUtc = now;
            }
            e.NextUtc = e.LastUtc + Interval;
            Save();
            return (e.Count, e.Count >= S.AliveDropAfterFailures);
        }
    }

    /// <summary>The check could not be trusted (our network failed too): try again later without counting.</summary>
    public static void Postpone(string key)
    {
        lock (_gate)
        {
            if (Map.TryGetValue(key, out var e))
            {
                e.NextUtc = DateTime.UtcNow + Interval;
                Save();
            }
        }
    }

    public static bool Clear(string key)
    {
        lock (_gate)
        {
            if (!Map.Remove(key))
            {
                return false;
            }
            Save();
            return true;
        }
    }

    /// <summary>Another network: failures seen on the previous one say nothing about this one.</summary>
    public static void ResetAll()
    {
        lock (_gate)
        {
            if (Map.Count == 0)
            {
                return;
            }
            Map.Clear();
            Save();
        }
    }

    /// <summary>Members due for a re-check (failed, not yet dropped, interval passed).</summary>
    public static List<string> DueKeys()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return Map.Where(kv => kv.Value.Count < S.AliveDropAfterFailures && kv.Value.NextUtc <= now).Select(kv => kv.Key).ToList();
        }
    }

    /// <summary>Forgets members that are no longer in the group.</summary>
    public static void Retain(IReadOnlySet<string> keys)
    {
        lock (_gate)
        {
            var gone = Map.Keys.Where(k => !keys.Contains(k)).ToList();
            if (gone.Count == 0)
            {
                return;
            }
            gone.ForEach(k => Map.Remove(k));
            Save();
        }
    }

    public static int Failing
    {
        get
        {
            lock (_gate)
            {
                return Map.Count;
            }
        }
    }

    private static void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Map));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // best effort
        }
    }
}
