using System.Runtime.InteropServices;
using System.Text.Json;

namespace V2RayTui.Cli;

public static class CliApp
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    public static async Task<int> RunAsync(string[] rawArgs)
    {
        var args = new ArgList(rawArgs);
        if (args.Get("--lang") is { } lang)
        {
            Loc.IsRu = lang.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        }
        AppHost.PrepareEnvironment(args.Has("--portable"), args.Get("--data") ?? Environment.GetEnvironmentVariable("V2RAYN_TUI_DATA"));

        var cmd = args.Pos(0)?.ToLowerInvariant() ?? "tui";
        if (args.Has("--version") || cmd is "version")
        {
            Console.WriteLine($"v2rayn-tui {AppHost.Version} (v2rayN engine {Utils.GetVersionInfo()})");
            return 0;
        }
        if (args.Has("--help") || cmd is "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return cmd switch
            {
                "tui" => await RunTuiAsync(args),
                "daemon" => await Daemon.RunAsync(args),
                "stop" => await StopAsync(),
                "import-data" => ImportData(args),
                "status" => Status(),
                "test" => await WithEngine("cli", () => TestAsync(args)),
                "list" or "ls" => await WithEngine("cli", () => ListAsync(args)),
                "sub" => await WithEngine("cli", () => SubAsync(args)),
                "import" => await WithEngine("cli", () => ImportAsync(args)),
                "use" => await WithEngine("cli", () => UseAsync(args)),
                "cycle" => await WithEngine("cli", async () =>
                {
                    // One background cycle in the foreground (e.g. from cron): subscriptions → tests → Alive.
                    await BackgroundScheduler.Instance.RunOnceAsync();
                    Console.WriteLine(BackgroundScheduler.Instance.LastResult);
                    return 0;
                }),
                "core" => await WithEngine("cli", () => CoreAsync(args)),
                "geo" => await WithEngine("cli", async () =>
                {
                    await CoreUpdater.UpdateGeoAsync(args.Has("--proxy"));
                    return 0;
                }),
                _ => Unknown(cmd),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine(L($"Unknown command: {cmd}", $"Неизвестная команда: {cmd}"));
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(L(
"""
v2rayn-tui — terminal UI for the v2rayN engine (xray / sing-box)

Usage: v2rayn-tui [--data DIR | --portable] [--lang en|ru] [command]

  (no command) | tui            interactive TUI
  daemon [--log FILE] [--no-core] [--test-now]
                                headless mode: keeps the proxy running, updates
                                subscriptions and tests servers in background
  stop                          stop the running daemon / TUI
  import-data DIR [--force]     copy data (subscriptions, servers, cores) from another data dir
  status                        show data dir, owner process, cores

  list [-s SUB]                 list servers with last results
  test [-s SUB] [-m MODE] [-n TOP] [--sort] [--json]
                                parallel test; MODE: tcping | ping | speed | pingspeed
  use ID|NUM|best [-s SUB]      make server active (best = lowest delay)
  cycle                         run one background cycle now (subscriptions → tests → Alive group)
  sub list
  sub add URL [--name NAME] [--interval MIN]
  sub update [SUB] [--proxy] [--force]
                                update one or all subscriptions (at most once per
                                SubUpdateMinIntervalMinutes, 60 by default; --force ignores it)
  sub rm SUB
  import FILE|-                 import share links (vmess://, vless://, ss://, …, base64)
  core update [xray|sing_box|all] [--proxy]
  geo update [--proxy]

SUB is a subscription id, its name or its number from `sub list`.
Data: ~/.local/share/v2rayN (shared with v2rayN desktop); --data DIR (or V2RAYN_TUI_DATA) = DIR/v2rayN;
--portable = next to the binary.
""",
"""
v2rayn-tui — терминальный интерфейс к движку v2rayN (xray / sing-box)

Использование: v2rayn-tui [--data КАТАЛОГ | --portable] [--lang en|ru] [команда]

  (без команды) | tui           интерактивный TUI
  daemon [--log ФАЙЛ] [--no-core] [--test-now]
                                фоновый режим: держит прокси, обновляет подписки
                                и тестирует серверы по расписанию
  stop                          остановить запущенный daemon / TUI
  import-data КАТАЛОГ [--force] перенести данные (подписки, серверы, ядра) из другого каталога
  status                        каталог данных, процесс-владелец, ядра

  list [-s ПОДП]                список серверов с последними результатами
  test [-s ПОДП] [-m РЕЖИМ] [-n TOP] [--sort] [--json]
                                параллельный тест; РЕЖИМ: tcping | ping | speed | pingspeed
  use ID|НОМЕР|best [-s ПОДП]   сделать сервер активным (best = мин. задержка)
  cycle                         выполнить фоновый цикл сейчас (подписки → тесты → группа Alive)
  sub list
  sub add URL [--name ИМЯ] [--interval МИН]
  sub update [ПОДП] [--proxy] [--force]
                                обновить одну или все подписки (не чаще раза в
                                SubUpdateMinIntervalMinutes, по умолчанию 60 мин; --force — игнорировать)
  sub rm ПОДП
  import ФАЙЛ|-                 импорт ссылок (vmess://, vless://, ss://, …, base64)
  core update [xray|sing_box|all] [--proxy]
  geo update [--proxy]

ПОДП — id подписки, её имя или номер из `sub list`.
Данные: ~/.local/share/v2rayN (общие с v2rayN desktop); --data КАТАЛОГ (или V2RAYN_TUI_DATA) — КАТАЛОГ/v2rayN;
--portable — рядом с бинарником.
"""));
    }

    private static async Task<int> RunTuiAsync(ArgList args)
    {
        var lk = InstanceLock.TryAcquire("tui", out var err);
        if (lk is null)
        {
            var owner = InstanceLock.ReadOwner();
            if (owner is { Mode: "daemon" })
            {
                Console.Write(L($"{err} Stop it and open the TUI? [Y/n] ", $"{err} Остановить его и открыть TUI? [Y/n] "));
                var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (answer is "" or "y" or "yes" or "д" or "да" or null)
                {
                    await InstanceLock.StopOwnerAsync(TimeSpan.FromSeconds(15));
                    lk = InstanceLock.TryAcquire("tui", out err);
                }
            }
            if (lk is null)
            {
                Console.Error.WriteLine(err);
                return 1;
            }
        }

        using (lk)
        {
            Console.WriteLine(L("Starting v2rayN engine…", "Запуск движка v2rayN…"));
            if (!await AppHost.InitAsync(registerScheduledTasks: true))
            {
                Console.Error.WriteLine(L("Failed to load configuration", "Не удалось загрузить конфигурацию"));
                return 1;
            }

            var result = Tui.TuiApp.Run();

            ProxyController.Instance.StopWatchdog();
            await BackgroundScheduler.Instance.StopAsync();
            TestService.Instance.StopAll();
            await AppHost.ShutdownAsync();

            if (result == Tui.TuiExit.Detach)
            {
                lk.Dispose();
                return Daemon.SpawnDetached();
            }
        }
        return 0;
    }

    /// <summary>Copies another data directory (e.g. a --portable one) into the current one.</summary>
    private static int ImportData(ArgList args)
    {
        var from = args.Pos(1) ?? throw new ArgumentException(L("Specify the directory that contains guiConfigs", "Укажите каталог, в котором лежит guiConfigs"));
        from = Path.GetFullPath(from);
        if (File.Exists(Path.Combine(from, "v2rayN", "guiConfigs", "guiNDB.db")))
        {
            from = Path.Combine(from, "v2rayN");
        }
        var srcConfigs = Path.Combine(from, "guiConfigs");
        if (!File.Exists(Path.Combine(srcConfigs, "guiNDB.db")))
        {
            Console.Error.WriteLine(L($"No v2rayN data in {from}", $"В {from} нет данных v2rayN"));
            return 1;
        }
        if (File.Exists(Path.Combine(srcConfigs, "tui.pid")) && int.TryParse(File.ReadLines(Path.Combine(srcConfigs, "tui.pid")).FirstOrDefault(), out var pid) && InstanceLock.IsAlive(pid))
        {
            Console.Error.WriteLine(L($"The source is in use (pid {pid}): quit that TUI first.", $"Источник используется (pid {pid}): сначала закройте тот TUI."));
            return 1;
        }
        var target = AppHost.DataDir;
        if (Path.GetFullPath(target).TrimEnd('/') == from.TrimEnd('/'))
        {
            Console.Error.WriteLine(L("Source and target are the same directory.", "Источник и цель совпадают."));
            return 1;
        }
        using var lk = InstanceLock.TryAcquire("cli", out var err);
        if (lk is null)
        {
            Console.Error.WriteLine(err);
            return 1;
        }
        if (File.Exists(Path.Combine(target, "guiConfigs", "guiNDB.db")) && !args.Has("--force"))
        {
            Console.Error.WriteLine(L($"{target} already has data. Use --force to replace it.", $"В {target} уже есть данные. --force — заменить."));
            return 1;
        }
        var skip = new HashSet<string> { "tui.lock", "tui.pid" };
        var copied = 0;
        foreach (var sub in new[] { "guiConfigs", "bin" })
        {
            var src = Path.Combine(from, sub);
            if (!Directory.Exists(src))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (skip.Contains(Path.GetFileName(file)))
                {
                    continue;
                }
                var dst = Path.Combine(target, sub, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(dst, File.GetUnixFileMode(file));
                }
                copied++;
            }
        }
        Console.WriteLine(L($"Copied {copied} files: {from} → {target}", $"Скопировано файлов: {copied}: {from} → {target}"));
        return 0;
    }

    private static async Task<int> StopAsync()
    {
        var owner = InstanceLock.ReadOwner();
        if (owner is null)
        {
            Console.WriteLine(L("Nothing is running.", "Ничего не запущено."));
            return 0;
        }
        Console.WriteLine(L($"Stopping {owner.Value.Mode} (pid {owner.Value.Pid})…", $"Остановка {owner.Value.Mode} (pid {owner.Value.Pid})…"));
        var ok = await InstanceLock.StopOwnerAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine(ok ? L("Stopped.", "Остановлено.") : L("Still running.", "Процесс ещё работает."));
        return ok ? 0 : 1;
    }

    private static int Status()
    {
        Console.WriteLine($"{L("Data dir", "Каталог данных")}: {AppHost.DataDir}");
        var owner = InstanceLock.ReadOwner();
        Console.WriteLine(owner is { } o
            ? $"{L("Running", "Запущен")}: {o.Mode} (pid {o.Pid})"
            : L("Running: no", "Запущен: нет"));
        foreach (var core in CoreUpdater.MainCores)
        {
            Console.WriteLine($"{core}: {(CoreUpdater.IsInstalled(core) ? L("installed", "установлено") : L("missing (run `core update`)", "нет (выполните `core update`)"))}");
        }
        var st = TuiSettings.Load();
        Console.WriteLine(st.AliveEnabled
            ? $"{st.AliveName}: {L("on", "вкл")}, ≥ {st.AliveMinSpeed} MB/s{(st.AliveMaxDelay > 0 ? $", ≤ {st.AliveMaxDelay} ms" : "")}, {L("every", "каждые")} {st.BackgroundIntervalMinutes} min"
            : $"Alive: {L("off", "выкл")}");
        Console.WriteLine($"geo: {(CoreUpdater.GeoFilesPresent ? L("installed", "установлено") : L("missing (run `geo update`)", "нет (выполните `geo update`)"))}");
        return 0;
    }

    /// <summary>Runs a one-shot command that needs exclusive access to the data dir.</summary>
    private static async Task<int> WithEngine(string mode, Func<Task<int>> body)
    {
        using var lk = InstanceLock.TryAcquire(mode, out var err);
        if (lk is null)
        {
            Console.Error.WriteLine(InstanceLock.ReadOwner() is null
                ? err
                : err + " " + L("Use `stop` first or run the command from the TUI.", "Сначала выполните `stop` или используйте TUI."));
            return 1;
        }
        LogBus.EchoToConsole = true;
        if (!await AppHost.InitAsync(registerScheduledTasks: false))
        {
            return 1;
        }
        try
        {
            return await body();
        }
        finally
        {
            LogBus.EchoToConsole = false;
            await AppHost.ShutdownAsync();
        }
    }

    public static async Task<SubItem?> ResolveSubAsync(string? key)
    {
        if (key.IsNullOrEmpty())
        {
            return null;
        }
        var subs = await AppManager.Instance.SubItems() ?? [];
        if (int.TryParse(key, out var n) && n >= 1 && n <= subs.Count)
        {
            return subs[n - 1];
        }
        return subs.FirstOrDefault(s => s.Id == key)
               ?? subs.FirstOrDefault(s => string.Equals(s.Remarks, key, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException(L($"Subscription not found: {key}", $"Подписка не найдена: {key}"));
    }

    private static async Task<List<ServerRow>> RowsAsync(ArgList args)
    {
        var sub = await ResolveSubAsync(args.Get("--sub"));
        return await ServerRepository.LoadAsync(sub?.Id, "");
    }

    private static string DelayText(ServerRow r) => r.Delay switch { > 0 => $"{r.Delay} ms", < 0 => "✗", _ => "-" };

    private static string SpeedText(ServerRow r) => r.Speed > 0 ? $"{r.Speed:0.0} MB/s" : r.SpeedStatus;

    private static void PrintRows(IReadOnlyList<ServerRow> rows, int limit)
    {
        Console.WriteLine($"{"#",4}  {"",1} {L("Delay", "Задержка"),9} {L("Speed", "Скорость"),11}  {L("Type", "Тип"),-12} {L("Name", "Имя"),-40} {L("Address", "Адрес")}");
        var i = 0;
        foreach (var r in rows)
        {
            if (limit > 0 && i >= limit)
            {
                Console.WriteLine($"… {rows.Count - limit} " + L("more", "ещё"));
                break;
            }
            i++;
            Console.WriteLine($"{i,4}  {(r.IsActive ? "*" : " ")} {DelayText(r),9} {SpeedText(r),11}  {r.TypeName,-12} {Trunc(r.Remarks, 40),-40} {r.Endpoint}");
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static async Task<int> ListAsync(ArgList args)
    {
        LogBus.EchoToConsole = false;
        var rows = await RowsAsync(args);
        PrintRows(rows, args.GetInt("--limit", 0));
        return 0;
    }

    public static TestMode ParseMode(string? s) => s?.ToLowerInvariant() switch
    {
        "tcping" or "tcp" => TestMode.Tcping,
        "speed" => TestMode.Speed,
        "pingspeed" or "mixed" or "ps" => TestMode.PingThenSpeed,
        _ => TestMode.RealPing,
    };

    private static async Task<int> TestAsync(ArgList args)
    {
        var mode = ParseMode(args.Get("--mode"));
        if (args.Get("--top") is { } top && int.TryParse(top, out var n))
        {
            AppHost.Settings.SpeedTopN = n;
        }
        if (mode != TestMode.Tcping && !CoreUpdater.MainCores.Any(CoreUpdater.IsInstalled))
        {
            Console.Error.WriteLine(L("No core installed: run `v2rayn-tui core update` first.", "Ядро не установлено: сначала выполните `v2rayn-tui core update`."));
            return 1;
        }
        var json = args.Has("--json");
        LogBus.EchoToConsole = false;

        var rows = await RowsAsync(args);
        var profiles = await ServerRepository.ToProfilesAsync(rows);
        var job = TestService.Instance.Start("cli", mode, profiles, background: false);

        using var sig = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            job.Cts.Cancel();
        });

        while (!job.Completion.IsCompleted)
        {
            if (!json && !Console.IsErrorRedirected)
            {
                Console.Error.Write($"\r{job.ProgressText}        ");
            }
            await Task.WhenAny(job.Completion, Task.Delay(500));
        }
        if (!json && !Console.IsErrorRedirected)
        {
            Console.Error.WriteLine($"\r{job.ProgressText}        ");
        }

        rows = await RowsAsync(args);
        var ordered = ServerRepository.OrderByResult(rows, mode is TestMode.Speed or TestMode.PingThenSpeed).ToList();
        if (args.Has("--sort"))
        {
            await ServerRepository.SaveOrderAsync(ordered);
        }

        if (json)
        {
            var data = ordered.Select(r => new
            {
                id = r.IndexId,
                name = r.Remarks,
                type = r.TypeName,
                address = r.Address,
                port = r.Port,
                subscription = r.SubRemarks,
                delay = r.Delay,
                speed = r.Speed,
                ip = r.IpInfo,
                active = r.IsActive,
            });
            Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            PrintRows(ordered, args.GetInt("--limit", 0));
        }
        return job.Cancelled ? 130 : 0;
    }

    private static async Task<int> UseAsync(ArgList args)
    {
        var key = args.Pos(1) ?? throw new ArgumentException(L("Specify server id, number or `best`", "Укажите id сервера, номер или `best`"));
        var rows = await RowsAsync(args);
        ServerRow? row = key == "best"
            ? ServerRepository.OrderByResult(rows, false).FirstOrDefault(r => r.Delay > 0)
            : int.TryParse(key, out var n) && n >= 1 && n <= rows.Count ? rows[n - 1] : rows.FirstOrDefault(r => r.IndexId == key);
        if (row is null)
        {
            Console.Error.WriteLine(L("Server not found", "Сервер не найден"));
            return 1;
        }
        await ConfigHandler.SetDefaultServerIndex(AppHost.Config, row.IndexId);
        Console.WriteLine($"{L("Active", "Активный")}: {row.Summary}");
        return 0;
    }

    private static async Task<int> SubAsync(ArgList args)
    {
        var config = AppHost.Config;
        switch (args.Pos(1))
        {
            case null or "list" or "ls":
            {
                LogBus.EchoToConsole = false;
                var subs = await AppManager.Instance.SubItems() ?? [];
                var i = 0;
                foreach (var s in subs)
                {
                    var count = (await AppManager.Instance.ProfileItemIndexes(s.Id))?.Count ?? 0;
                    var updated = s.UpdateTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.UpdateTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "-";
                    Console.WriteLine($"{++i,3}. {(s.Enabled ? " " : "-")} {s.Remarks,-24} {count,5} {L("servers", "серв.")}  {L("upd", "обн.")} {updated}  auto {s.AutoUpdateInterval}m  {s.Url}");
                }
                return 0;
            }
            case "add":
            {
                var url = args.Pos(2) ?? throw new ArgumentException("URL?");
                var item = new SubItem
                {
                    Id = string.Empty,
                    Url = url,
                    Remarks = args.Get("--name") ?? Utils.ParseQueryString(Utils.TryUri(url)?.Query ?? "")["remarks"] ?? "import_sub",
                    AutoUpdateInterval = args.GetInt("--interval", 0),
                };
                if (item.AutoUpdateInterval > 0 && item.AutoUpdateInterval < AppHost.Settings.SubUpdateMinIntervalMinutes)
                {
                    item.AutoUpdateInterval = AppHost.Settings.SubUpdateMinIntervalMinutes;
                    Console.WriteLine(L($"Auto update interval raised to {item.AutoUpdateInterval} min (rate limit)", $"Интервал автообновления поднят до {item.AutoUpdateInterval} мин (ограничение частоты)"));
                }
                var ret = await ConfigHandler.AddSubItem(config, item);
                Console.WriteLine(ret == 0 ? L("Added.", "Добавлено.") : L("Failed.", "Ошибка."));
                return ret == 0 ? 0 : 1;
            }
            case "update" or "up":
            {
                var sub = await ResolveSubAsync(args.Pos(2));
                var r = await ProxyController.Instance.UpdateSubscriptionsAsync(sub?.Id, args.Has("--proxy"), args.Has("--force"));
                if (r.Skipped.Count > 0 && r.Updated == 0 && r.Failed == 0)
                {
                    Console.WriteLine(L("Nothing to update yet (rate limit). Use --force to override.", "Обновлять пока нечего (ограничение частоты). --force — принудительно."));
                }
                return r.Failed > 0 ? 1 : 0;
            }
            case "rm" or "del" or "remove":
            {
                var sub = await ResolveSubAsync(args.Pos(2)) ?? throw new ArgumentException("SUB?");
                await ConfigHandler.DeleteSubItem(config, sub.Id);
                Console.WriteLine(L("Removed.", "Удалено."));
                return 0;
            }
            default:
                return Unknown("sub " + args.Pos(1));
        }
    }

    private static async Task<int> ImportAsync(ArgList args)
    {
        var src = args.Pos(1) ?? "-";
        var text = src == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(src);
        var sub = await ResolveSubAsync(args.Get("--sub"));
        var count = await ImportText(text, sub?.Id);
        Console.WriteLine(L($"Imported: {Math.Max(0, count)}", $"Импортировано: {Math.Max(0, count)}"));
        return count > 0 ? 0 : 1;
    }

    /// <summary>Same as v2rayN's "Import from clipboard": share links, base64 blobs, or a subscription URL.</summary>
    public static async Task<int> ImportText(string text, string? subId)
    {
        text = text.Trim();
        if (text.IsNullOrEmpty())
        {
            return 0;
        }
        if ((text.StartsWith(Global.HttpsProtocol) || text.StartsWith(Global.HttpProtocol)) && !text.Contains('\n'))
        {
            return await ConfigHandler.AddSubItem(AppHost.Config, text) == 0 ? 1 : 0;
        }
        return await ConfigHandler.AddBatchServers(AppHost.Config, text, subId ?? "", false);
    }

    private static async Task<int> CoreAsync(ArgList args)
    {
        if (args.Pos(1) != "update")
        {
            return Unknown("core " + args.Pos(1));
        }
        var which = args.Pos(2)?.ToLowerInvariant() ?? "all";
        ECoreType[] cores = which switch
        {
            "xray" => [ECoreType.Xray],
            "sing_box" or "sing-box" or "singbox" => [ECoreType.sing_box],
            "all" => CoreUpdater.MainCores,
            _ => throw new ArgumentException(which),
        };
        var ok = true;
        foreach (var c in cores)
        {
            ok &= await CoreUpdater.UpdateCoreAsync(c, args.Has("--proxy"));
        }
        if (which == "all")
        {
            await CoreUpdater.UpdateGeoAsync(args.Has("--proxy"));
            ok &= CoreUpdater.GeoFilesPresent;
        }
        return ok ? 0 : 1;
    }
}
