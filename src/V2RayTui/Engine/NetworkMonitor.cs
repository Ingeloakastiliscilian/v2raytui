namespace V2RayTui.Engine;

/// <summary>The network we are attached to: default route, and the DNS servers / search domains it gave us.</summary>
public sealed record NetworkInfo(string Interface, string Gateway, IReadOnlyList<string> DnsServers, IReadOnlyList<string> SearchDomains)
{
    public static readonly NetworkInfo None = new("", "", [], []);

    public bool IsUp => Interface.IsNotEmpty();

    /// <summary>Changes when we move to another network (or it hands out other DNS settings).</summary>
    public string Fingerprint => $"{Interface}|{Gateway}|{string.Join(",", DnsServers)}|{string.Join(",", SearchDomains)}";

    public override string ToString() => IsUp
        ? $"{Interface} via {Gateway}; DNS {(DnsServers.Count > 0 ? string.Join(", ", DnsServers) : "-")}"
          + (SearchDomains.Count > 0 ? $"; {string.Join(", ", SearchDomains)}" : "")
        : Loc.T("no network", "нет сети");
}

/// <summary>
/// Polls the host network (Linux): default route and the DNS it got from DHCP / NetworkManager.
/// Our own TUN, containers and bridges are ignored, so restarting the core is not a "network change".
/// </summary>
public sealed class NetworkMonitor
{
    public static NetworkMonitor Instance { get; } = new();

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // A new network must look the same for this long before it counts (DHCP / VPN clients change the DNS list
    // in steps; one Wi-Fi seen 29.09 flipped its DNS every 10 s).
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(20);

    private CancellationTokenSource? _cts;
    private string _candidate = "";
    private DateTime _candidateSince;
    private NetworkInfo _lastUp = NetworkInfo.None;

    public NetworkInfo Current { get; private set; } = NetworkInfo.None;

    /// <summary>Raised (on a worker thread) with (old, new) when we are on another network and it has settled.</summary>
    public event Action<NetworkInfo, NetworkInfo>? Changed;

    /// <summary>
    /// The same network is back after a short loss (Wi-Fi reconnect, resume): nothing to rebuild, except what
    /// was built while it was gone.
    /// </summary>
    public event Action<NetworkInfo>? Restored;

    public void Start()
    {
        if (_cts != null || !OperatingSystem.IsLinux())
        {
            return;
        }
        _cts = new CancellationTokenSource();
        Current = Read();
        _lastUp = Current;
        LogBus.WriteFileOnly($"[net] {Current}");
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, ct);
                    Poll();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogBus.WriteFileOnly($"[net] {ex.Message}");
                }
            }
        }, ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private void Poll()
    {
        var now = Read();
        if (now.Fingerprint == Current.Fingerprint)
        {
            _candidate = "";
            return;
        }
        if (!now.IsUp)
        {
            // Lost: remember it, act only on what comes back.
            _candidate = "";
            if (Current.IsUp)
            {
                Current = now;
                LogBus.Write("[net] " + Loc.T("network: none", "сеть: нет сети"));
            }
            return;
        }
        if (now.Fingerprint != _candidate)
        {
            _candidate = now.Fingerprint;
            _candidateSince = DateTime.UtcNow;
            // The network we had before a short loss: no need to wait for it to settle.
            if (now.Fingerprint != _lastUp.Fingerprint)
            {
                return;
            }
        }
        else if (DateTime.UtcNow - _candidateSince < SettleTime && now.Fingerprint != _lastUp.Fingerprint)
        {
            return;
        }
        var old = _lastUp;
        Current = now;
        _lastUp = now;
        _candidate = "";
        if (now.Fingerprint == old.Fingerprint)
        {
            LogBus.Write("[net] " + Loc.T($"network is back: {now}", $"сеть вернулась: {now}"));
            Restored?.Invoke(now);
            return;
        }
        LogBus.Write("[net] " + Loc.T($"network: {now}", $"сеть: {now}"));
        Changed?.Invoke(old, now);
    }

    // Our TUN, VPN-less virtual links: never "the network".
    private static bool IsVirtual(string iface) =>
        iface is "lo" || iface.StartsWith("singbox") || iface.StartsWith("xray") || iface.StartsWith("tun")
        || iface.StartsWith("docker") || iface.StartsWith("br-") || iface.StartsWith("veth") || iface.StartsWith("virbr");

    public static NetworkInfo Read()
    {
        // Debugging aid: a file "iface gateway dns1,dns2 domain1,domain2" stands for the network.
        if (Environment.GetEnvironmentVariable("V2RAYN_TUI_NETWORK_FILE") is { Length: > 0 } fake)
        {
            var f = File.Exists(fake) ? File.ReadAllText(fake).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
            return f.Length < 2
                ? NetworkInfo.None
                : new NetworkInfo(f[0], f[1], f.Length > 2 ? f[2].Split(',') : [], f.Length > 3 ? f[3].Split(',') : []);
        }
        if (!OperatingSystem.IsLinux())
        {
            return NetworkInfo.None;
        }
        var (iface, gateway) = DefaultRoute();
        if (iface.IsNullOrEmpty())
        {
            return NetworkInfo.None;
        }
        var (dns, domains) = ResolvedLinks(iface) ?? ResolvConf();
        return new NetworkInfo(iface, gateway, dns, domains);
    }

    /// <summary>Default route of the main table with the lowest metric (/proc/net/route, hex little-endian).</summary>
    private static (string Iface, string Gateway) DefaultRoute()
    {
        try
        {
            var best = (Iface: "", Gateway: "", Metric: int.MaxValue);
            foreach (var line in File.ReadLines("/proc/net/route").Skip(1))
            {
                var f = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 7 || f[1] != "00000000" || IsVirtual(f[0]) || !int.TryParse(f[6], out var metric))
                {
                    continue;
                }
                if (metric < best.Metric)
                {
                    var gw = Convert.ToUInt32(f[2], 16);
                    best = (f[0], new System.Net.IPAddress(gw).ToString(), metric);
                }
            }
            return (best.Iface, best.Gateway);
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>
    /// systemd-resolved: per-link DNS servers and domains (`resolvectl dns` / `resolvectl domain`).
    /// The default-route link first, then the other physical links, then the global ones.
    /// </summary>
    private static (List<string>, List<string>)? ResolvedLinks(string defaultIface)
    {
        var dnsOut = Run("resolvectl", "dns");
        if (dnsOut is null)
        {
            return null;
        }
        var domOut = Run("resolvectl", "domain") ?? "";

        static Dictionary<string, List<string>> Parse(string text)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var line in text.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                var head = line[..colon].Trim();
                // "Global" or "Link 3 (wlo1)"
                var name = head == "Global" ? "" : head.Contains('(') ? head[(head.IndexOf('(') + 1)..].TrimEnd(')') : head;
                map[name] = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            return map;
        }

        var dnsMap = Parse(dnsOut);
        var domMap = Parse(domOut);
        var order = dnsMap.Keys.Where(k => k == defaultIface)
            .Concat(dnsMap.Keys.Where(k => k != defaultIface && k != "" && !IsVirtual(k)))
            .Concat(dnsMap.Keys.Where(k => k == ""))
            .ToList();
        var dns = order.SelectMany(k => dnsMap[k]).Select(CleanServer).Where(IsUsableServer).Distinct().ToList();
        var domains = order.SelectMany(k => domMap.GetValueOrDefault(k) ?? []).Select(d => d.TrimStart('~').TrimEnd('.'))
            .Where(d => d.IsNotEmpty() && d != ".").Distinct().ToList();
        return dns.Count > 0 ? (dns, domains) : null;
    }

    /// <summary>Without resolved: the upstream list resolved writes, or /etc/resolv.conf itself.</summary>
    private static (List<string>, List<string>) ResolvConf()
    {
        foreach (var path in new[] { "/run/systemd/resolve/resolv.conf", "/etc/resolv.conf" })
        {
            try
            {
                var dns = new List<string>();
                var domains = new List<string>();
                foreach (var raw in File.ReadLines(path))
                {
                    var f = raw.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 2 || f[0].StartsWith('#'))
                    {
                        continue;
                    }
                    if (f[0] == "nameserver" && IsUsableServer(CleanServer(f[1])))
                    {
                        dns.Add(CleanServer(f[1]));
                    }
                    else if (f[0] is "search" or "domain")
                    {
                        domains.AddRange(f.Skip(1).Select(d => d.TrimEnd('.')));
                    }
                }
                if (dns.Count > 0)
                {
                    return (dns.Distinct().ToList(), domains.Distinct().ToList());
                }
            }
            catch
            {
                // next
            }
        }
        return ([], []);
    }

    // resolvectl may print "10.0.0.1#dns.example" (DoT server name) or "[fe80::1%3]:53".
    private static string CleanServer(string s) => s.Split('#')[0].Trim();

    private static bool IsUsableServer(string s) =>
        System.Net.IPAddress.TryParse(s, out var ip)
        && !System.Net.IPAddress.IsLoopback(ip)
        && !(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal) // needs a scope id
        && !ProxyController.InTunSubnet(s); // our own TUN's DNS

    private static string? Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.Environment["LANG"] = "C";
            psi.Environment["SYSTEMD_COLORS"] = "0";
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                p.Kill();
                return null;
            }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
