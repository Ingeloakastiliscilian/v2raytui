namespace V2RayTui.Engine;

/// <summary>Downloads and installs cores / geo files through v2rayN's UpdateService.</summary>
public static class CoreUpdater
{
    public static readonly ECoreType[] MainCores = [ECoreType.Xray, ECoreType.sing_box];

    public static bool IsInstalled(ECoreType type)
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(type);
        return info?.CoreExes?.Any(name => File.Exists(Utils.GetBinPath(Utils.GetExeName(name), type.ToString()))) == true;
    }

    /// <summary>Oldest core versions the bundled engine (v2rayN 7.25) generates configs for.</summary>
    private static readonly Dictionary<ECoreType, Version> MinVersions = new()
    {
        [ECoreType.sing_box] = new Version(1, 14, 0), // 7.25 writes dns.rules[].preferred_by
        [ECoreType.Xray] = new Version(26, 1, 0),
    };

    public static Version? InstalledVersion(ECoreType type)
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(type);
        var exe = CoreInfoManager.Instance.GetCoreExecFile(info, out _);
        if (exe.IsNullOrEmpty() || !File.Exists(exe))
        {
            return null;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\.(\d+)\.(\d+)");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Warns about cores too old for the engine's configs (typical when data is shared with an older v2rayN).</summary>
    public static void WarnOutdatedCores()
    {
        foreach (var (type, min) in MinVersions)
        {
            if (InstalledVersion(type) is { } v && v < min)
            {
                LogBus.Notice(Loc.T($"{type} {v} is too old for this engine (needs ≥ {min}): update it — F8 or `v2rayn-tui core update`",
                    $"{type} {v} слишком старое для этого движка (нужно ≥ {min}): обновите — F8 или `v2rayn-tui core update`"));
            }
        }
    }

    public static bool GeoFilesPresent =>
        File.Exists(Utils.GetBinPath("geosite.dat")) && File.Exists(Utils.GetBinPath("geoip.dat"));

    /// <summary>What must be downloaded before the proxy can work (empty = ready).</summary>
    public static List<string> MissingComponents()
    {
        var list = MainCores.Where(c => !IsInstalled(c)).Select(c => c.ToString()).ToList();
        if (!GeoFilesPresent)
        {
            list.Add("geo files");
        }
        return list;
    }

    private static Task<bool>? _installTask;

    /// <summary>A first-run download is in progress (the interface waits for it instead of starting another).</summary>
    public static bool Installing => _installTask is { IsCompleted: false };

    public static Task WaitInstallAsync() => _installTask ?? Task.CompletedTask;

    /// <summary>Downloads missing cores and geo files (first run).</summary>
    public static Task<bool> InstallMissingAsync(bool viaProxy, CancellationToken ct = default)
    {
        if (Installing)
        {
            return _installTask!;
        }
        _installTask = InstallMissingCoreAsync(viaProxy, ct);
        return _installTask;
    }

    private static async Task<bool> InstallMissingCoreAsync(bool viaProxy, CancellationToken ct)
    {
        var ok = true;
        foreach (var core in MainCores.Where(c => !IsInstalled(c)))
        {
            ok &= await UpdateCoreAsync(core, viaProxy, ct: ct);
        }
        if (!GeoFilesPresent)
        {
            await UpdateGeoAsync(viaProxy, ct);
            ok &= GeoFilesPresent;
        }
        return ok;
    }

    public static async Task<bool> UpdateCoreAsync(ECoreType type, bool viaProxy, bool preRelease = false, CancellationToken ct = default)
    {
        string? archive = null;
        var log = ThrottledLog($"[{type}]");
        var svc = new UpdateService(AppHost.Config, (success, msg) =>
        {
            if (success)
            {
                archive = msg;
            }
            else
            {
                log(msg);
            }
            return Task.CompletedTask;
        });

        await svc.CheckUpdateCore(type, preRelease, viaProxy, ct);
        for (var i = 0; i < 50 && archive is null; i++)
        {
            await Task.Delay(100, ct);
        }
        if (archive is null || !File.Exists(archive))
        {
            return false;
        }

        var wasRunning = ProxyController.Instance.CoreRunning;
        if (wasRunning)
        {
            await ProxyController.Instance.StopAsync();
        }
        try
        {
            Install(type, archive);
            LogBus.Notice($"[{type}] {ResUI.MsgUpdateV2rayCoreSuccessfully}");
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CoreUpdater", ex);
            LogBus.Notice($"[{type}] {ex.Message}");
            return false;
        }
        finally
        {
            File.Delete(archive);
            if (wasRunning)
            {
                await ProxyController.Instance.ReloadAsync();
            }
        }
    }

    // Same unpacking rules as v2rayN's CheckUpdateViewModel.UpgradeCore.
    private static void Install(ECoreType type, string archive)
    {
        var coreTypeStr = type.ToString();
        var toPath = Utils.GetBinPath("", coreTypeStr);

        if (archive.Contains(".tar.gz"))
        {
            FileUtils.DecompressTarFile(archive, toPath);
            foreach (var subDir in new DirectoryInfo(toPath).GetDirectories())
            {
                FileUtils.CopyDirectory(subDir.FullName, toPath, false, true);
                subDir.Delete(true);
            }
        }
        else if (archive.Contains(".gz"))
        {
            FileUtils.DecompressFile(archive, toPath, coreTypeStr);
        }
        else
        {
            FileUtils.ZipExtractToFile(archive, toPath, "geo");
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in new DirectoryInfo(toPath).GetFiles())
            {
                // Core executables have no extension; skip geo data, licenses, etc.
                if (file.Extension != "")
                {
                    continue;
                }
                File.SetUnixFileMode(file.FullName, File.GetUnixFileMode(file.FullName)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
    }

    public static async Task UpdateGeoAsync(bool viaProxy, CancellationToken ct = default)
    {
        var log = ThrottledLog("[geo]");
        await new UpdateService(AppHost.Config, (_, msg) =>
        {
            log(msg);
            return Task.CompletedTask;
        }).UpdateGeoFileAll(viaProxy, ct);
    }

    /// <summary>Download progress arrives many times per second: keep status lines, sample progress lines.</summary>
    private static Action<string> ThrottledLog(string prefix)
    {
        var last = "";
        var lastProgress = DateTime.MinValue;
        return msg =>
        {
            if (msg.IsNullOrEmpty() || msg == last)
            {
                return;
            }
            var isProgress = msg.Contains("/s |");
            if (isProgress && DateTime.Now - lastProgress < TimeSpan.FromSeconds(2))
            {
                return;
            }
            if (isProgress)
            {
                lastProgress = DateTime.Now;
            }
            last = msg;
            LogBus.Write($"{prefix} {msg}");
        };
    }
}
