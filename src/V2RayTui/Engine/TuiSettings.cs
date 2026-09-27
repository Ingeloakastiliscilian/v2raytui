using System.Text.Json;
using System.Text.Json.Serialization;

namespace V2RayTui.Engine;

public enum TestMode
{
    Tcping,
    RealPing,
    Speed,

    /// <summary>Real ping everything, then download-test the fastest alive servers.</summary>
    PingThenSpeed,
}

public enum AutoSwitchMode
{
    Off,

    /// <summary>Switch only when the active server stopped answering.</summary>
    Failover,

    /// <summary>Switch whenever another server is noticeably faster.</summary>
    Fastest,
}

/// <summary>TUI-specific settings, stored next to v2rayN's guiNConfig.json (guiConfigs/tui.json).</summary>
public sealed class TuiSettings
{
    // --- parallel test engine ---

    /// <summary>Servers per core process (each gets its own local socks inbound).</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How many core processes may run tests at the same time.</summary>
    public int ParallelCores { get; set; } = 4;

    /// <summary>Global limit of simultaneous ping / tcping probes.</summary>
    public int PingConcurrency { get; set; } = 64;

    /// <summary>Global limit of simultaneous download speed tests.</summary>
    public int SpeedConcurrency { get; set; } = 3;

    /// <summary>
    /// Speed tests at once in background cycles. 1 = one at a time: every server gets the whole link,
    /// so results are comparable between servers and cycles (pings still run in parallel).
    /// </summary>
    public int BackgroundSpeedConcurrency { get; set; } = 1;

    /// <summary>In PingThenSpeed mode: how many fastest alive servers get a download test (0 = all alive).</summary>
    public int SpeedTopN { get; set; } = 10;

    /// <summary>Query exit IP / country for alive servers.</summary>
    public bool QueryIpInfo { get; set; }

    /// <summary>Re-sort the list by test results after a manual test finishes.</summary>
    public bool SortAfterTest { get; set; }

    // --- background scheduler ---

    public bool BackgroundEnabled { get; set; } = true;

    public int BackgroundIntervalMinutes { get; set; } = 30;

    public TestMode BackgroundMode { get; set; } = TestMode.RealPing;

    /// <summary>Subscription id to test in background; empty = all servers.</summary>
    public string BackgroundSubId { get; set; } = "";

    /// <summary>Update subscriptions (through proxy when connected) before each background test.</summary>
    public bool UpdateSubsBeforeTest { get; set; }

    /// <summary>Run a background test of a subscription right after it has been updated.</summary>
    public bool TestAfterSubUpdate { get; set; } = true;

    public AutoSwitchMode AutoSwitch { get; set; } = AutoSwitchMode.Off;

    /// <summary>For <see cref="AutoSwitchMode.Fastest"/>: minimal improvement in percent to switch.</summary>
    public int SwitchThresholdPercent { get; set; } = 30;

    /// <summary>Restrict auto switching to servers of this subscription; empty = any server tested.</summary>
    public string AutoSwitchSubId { get; set; } = "";

    // --- subscriptions ---

    /// <summary>
    /// A subscription is requested at most once per this many minutes — any update: background, manual, CLI,
    /// and v2rayN's own auto update (its per-subscription intervals are raised to this value).
    /// Failed attempts count too (they reach the provider as well).
    /// </summary>
    public int SubUpdateMinIntervalMinutes { get; set; } = 60;

    // --- Alive group ---

    /// <summary>
    /// Maintain the "Alive" group: copies of servers that answered and reached <see cref="AliveMinSpeed"/>
    /// in the last background cycle. It has no URL, so subscription updates never touch it.
    /// </summary>
    public bool AliveEnabled { get; set; }

    public string AliveName { get; set; } = "Alive";

    /// <summary>Id of the group (managed automatically).</summary>
    public string AliveSubId { get; set; } = "";

    /// <summary>Minimal download speed, MB/s.</summary>
    public decimal AliveMinSpeed { get; set; } = 1m;

    /// <summary>Maximal real delay, ms (0 = no limit).</summary>
    public int AliveMaxDelay { get; set; }

    // --- misc ---

    /// <summary>Show core process output in the log (noisy during mass tests).</summary>
    public bool ShowCoreOutput { get; set; }

    [JsonIgnore]
    public static string FilePath => Utils.GetConfigPath("tui.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TuiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<TuiSettings>(File.ReadAllText(FilePath), _json);
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            LogBus.Write($"tui.json: {ex.Message}");
        }
        return new TuiSettings();
    }

    public void Save()
    {
        Normalize();
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, _json));
        File.Move(tmp, FilePath, true);
    }

    public void Normalize()
    {
        BatchSize = Math.Clamp(BatchSize, 1, 1000);
        ParallelCores = Math.Clamp(ParallelCores, 1, 32);
        PingConcurrency = Math.Clamp(PingConcurrency, 1, 1024);
        SpeedConcurrency = Math.Clamp(SpeedConcurrency, 1, 32);
        BackgroundSpeedConcurrency = Math.Clamp(BackgroundSpeedConcurrency, 1, 32);
        SpeedTopN = Math.Max(0, SpeedTopN);
        BackgroundIntervalMinutes = Math.Clamp(BackgroundIntervalMinutes, 1, 7 * 24 * 60);
        SwitchThresholdPercent = Math.Clamp(SwitchThresholdPercent, 0, 95);
        BackgroundSubId ??= "";
        AutoSwitchSubId ??= "";
        SubUpdateMinIntervalMinutes = Math.Clamp(SubUpdateMinIntervalMinutes, 1, 7 * 24 * 60);
        AliveName = string.IsNullOrWhiteSpace(AliveName) ? "Alive" : AliveName.Trim();
        AliveSubId ??= "";
        AliveMinSpeed = Math.Max(0, AliveMinSpeed);
        AliveMaxDelay = Math.Max(0, AliveMaxDelay);
    }
}
