using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace V2RayTui.Tui;

/// <summary>Simple vertical "label: control" form in a modal dialog.</summary>
internal sealed class Form : IDisposable
{
    private readonly Dialog _dlg;
    private readonly int _labelWidth;
    private int _y = 1;
    private View? _first;
    private bool _buttonsAdded;

    /// <summary>Checked when "Save" is pressed; a non-null message keeps the dialog open.</summary>
    public Func<string?>? Validate { get; set; }

    private readonly int _width;

    public Form(string title, int labelWidth = 28, int width = 90)
    {
        _labelWidth = labelWidth;
        _width = width;
        _dlg = new Dialog { BorderStyle = LineStyle.Rounded, Title = title, Width = width, Height = 6 };
    }

    public void Section(string title)
    {
        if (_y > 1)
        {
            _y++;
        }
        _dlg.Add(new Label { Text = $"── {title} ──", X = 1, Y = _y++ });
    }

    public TextField Text(string label, string? value, bool secret = false)
    {
        AddLabel(label);
        var tf = new TextField { X = _labelWidth + 2, Y = _y++, Width = Dim.Fill(2), Text = value ?? "", Secret = secret };
        return Track(tf);
    }

    public TextField Number(string label, int value) => Text(label, value.ToString());

    public CheckBox Check(string label, bool value)
    {
        var cb = new CheckBox { Text = label, X = _labelWidth + 2, Y = _y++, Value = value ? CheckState.Checked : CheckState.UnChecked };
        return Track(cb);
    }

    public OptionSelector<T> Options<T>(string label, T value) where T : struct, Enum
    {
        AddLabel(label);
        var os = new OptionSelector<T> { X = _labelWidth + 2, Y = _y++, Orientation = Orientation.Horizontal, Value = value };
        return Track(os);
    }

    /// <summary>A button showing the current choice; pressing it opens a list.</summary>
    public Func<int> Picker(string label, IReadOnlyList<string> items, int selected)
    {
        AddLabel(label);
        var current = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
        // No shadow: it would be drawn over the next row of the form.
        var btn = new Button { X = _labelWidth + 2, Y = _y++, Text = items.Count > 0 ? items[current] : "-", ShadowStyle = ShadowStyles.None };
        btn.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (_dlg.App is { } app && Dialogs.Choose(app, label, items, current) is { } i)
            {
                current = i;
                btn.Text = items[i];
            }
        };
        Track(btn);
        return () => current;
    }

    /// <summary>A text field with a "▾" button that fills it from ready-made values (free text still allowed).</summary>
    public TextField Combo(string label, string? value, IReadOnlyList<string> presets)
    {
        AddLabel(label);
        var tf = new TextField { X = _labelWidth + 2, Y = _y, Width = Dim.Fill(9), Text = value ?? "" };
        var btn = new Button { X = Pos.Right(tf) + 1, Y = _y++, Text = "▾", ShadowStyle = ShadowStyles.None };
        btn.Accepting += (_, e) =>
        {
            e.Handled = true;
            var items = presets.Select(x => x.IsNullOrEmpty() ? Loc.T("(empty)", "(пусто)") : x).ToList();
            var current = presets.ToList().IndexOf(tf.Text.Trim());
            if (_dlg.App is { } app && Dialogs.Choose(app, label, items, Math.Max(0, current)) is { } i)
            {
                tf.Text = presets[i];
                tf.SetFocus();
            }
        };
        Track(tf);
        Track(btn);
        return tf;
    }

    /// <summary><see cref="Picker"/> over string values; "" is shown as "(not set)".</summary>
    public Func<string> Choice(string label, IReadOnlyList<string> values, string? value)
    {
        var shown = values.Select(x => x.IsNullOrEmpty() ? Loc.T("(not set)", "(не задано)") : x).ToList();
        var pick = Picker(label, shown, Math.Max(0, values.ToList().IndexOf(value ?? "")));
        return () => values[pick()];
    }

#pragma warning disable CS0618 // TextView is obsolete in favour of an external editor package, but fine here.
    /// <summary>Multi-line text (one item per line).</summary>
    public TextView MultiText(string label, string? value, int height)
    {
        AddLabel(label);
        var tv = new TextView { X = _labelWidth + 2, Y = _y, Width = Dim.Fill(2), Height = height, Text = value ?? "", WordWrap = false, TabKeyAddsTab = false };
        _y += height;
        return Track(tv);
    }
#pragma warning restore CS0618

    /// <summary>A row of check boxes; returns the checked option names.</summary>
    public Func<List<string>> Flags(string label, IReadOnlyList<string> options, IReadOnlyCollection<string>? selected)
    {
        AddLabel(label);
        var x = _labelWidth + 2;
        var boxes = new List<(string Name, CheckBox Box)>();
        foreach (var o in options)
        {
            var cb = new CheckBox { Text = o, X = x, Y = _y, Value = selected?.Contains(o) == true ? CheckState.Checked : CheckState.UnChecked };
            Track(cb);
            boxes.Add((o, cb));
            x += o.Length + 6;
        }
        _y++;
        return () => boxes.Where(b => b.Box.Value == CheckState.Checked).Select(b => b.Name).ToList();
    }

    /// <summary>A button whose text is a value; pressing it calls <paramref name="edit"/> to pick a new one.</summary>
    public Func<string> Value(string label, string value, Func<IApplication, string, string?> edit)
    {
        AddLabel(label);
        var current = value;
        var btn = new Button { X = _labelWidth + 2, Y = _y++, Text = current.IsNullOrEmpty() ? "-" : current };
        btn.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (_dlg.App is { } app && edit(app, current) is { } picked)
            {
                current = picked;
                btn.Text = picked.IsNullOrEmpty() ? "-" : picked;
            }
        };
        Track(btn);
        return () => current;
    }

    /// <summary>Several action buttons on one row, after a label; an action may return a new button text.</summary>
    public void Actions(string label, params (string Text, Func<IApplication, string?> Action)[] actions)
    {
        AddLabel(label);
        View? prev = null;
        foreach (var (text, action) in actions)
        {
            var btn = new Button { X = prev is null ? _labelWidth + 2 : Pos.Right(prev) + 1, Y = _y, Text = text, ShadowStyle = ShadowStyles.None };
            btn.Accepting += (_, e) =>
            {
                e.Handled = true;
                if (_dlg.App is { } app && action(app) is { } newText)
                {
                    btn.Text = newText;
                }
            };
            Track(btn);
            prev = btn;
        }
        _y++;
    }

    /// <summary>A plain action button on its own row (e.g. "pick from running processes").</summary>
    public void Action(string text, Action<IApplication> action)
    {
        var btn = new Button { X = _labelWidth + 2, Y = _y++, Text = text, ShadowStyle = ShadowStyles.None };
        btn.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (_dlg.App is { } app)
            {
                action(app);
            }
        };
        Track(btn);
    }

    public void Note(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            _dlg.Add(new Label { Text = line, X = 2, Y = _y++ });
        }
    }

    public bool Run(IApplication app)
    {
        _dlg.Height = Dim.Func(_ => Math.Min(_y + 6, app.Screen.Height - 2));
        _dlg.Width = Dim.Func(_ => Math.Min(_width, app.Screen.Width - 2));
        if (!_buttonsAdded)
        {
            _buttonsAdded = true;
            _dlg.AddButton(new Button { Title = Loc.T("_Cancel", "_Отмена") });
            // Default: Enter in a field saves (Space toggles check boxes, Tab moves).
            var save = new Button { Title = Loc.T("_Save", "_Сохранить"), IsDefault = true };
            save.Accepting += (_, e) =>
            {
                if (Validate?.Invoke() is { } error)
                {
                    Dialogs.Error(app, _dlg.Title, error);
                    e.Handled = true;
                }
            };
            _dlg.AddButton(save);
        }
        _first?.SetFocus();
        app.Run(_dlg);
        return !_dlg.Canceled;
    }

    private void AddLabel(string label) =>
        _dlg.Add(new Label { Text = label, X = 1, Y = _y, Width = _labelWidth });

    private T Track<T>(T view) where T : View
    {
        _dlg.Add(view);
        _first ??= view;
        return view;
    }

    public static int Int(TextField tf, int fallback) => int.TryParse(tf.Text.Trim(), out var v) ? v : fallback;

    public static bool Bool(CheckBox cb) => cb.Value == CheckState.Checked;

    public void Dispose() => _dlg.Dispose();
}

internal static class SubEditDialog
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    /// <summary>Edits a subscription in place (fields of v2rayN's "Subscription settings").</summary>
    public static bool Run(IApplication app, SubItem item)
    {
        using var f = new Form(item.Id.IsNullOrEmpty() ? L("Add subscription", "Новая подписка") : L("Edit subscription", "Подписка"), 24);
        var remarks = f.Text(L("Name", "Имя"), item.Remarks);
        var url = f.Text("URL", item.Url);
        var more = f.Text(L("More URLs (comma sep.)", "Доп. URL (через запятую)"), item.MoreUrl);
        var enabled = f.Check(L("Enabled", "Включена"), item.Enabled);
        var interval = f.Number(L("Auto update, min (0=off)", "Автообновление, мин (0=выкл)"), item.AutoUpdateInterval);
        var filter = f.Text(L("Filter (regex on name)", "Фильтр (regex по имени)"), item.Filter);
        var ua = f.Text("User-Agent", item.UserAgent);
        var convert = f.Text(L("Convert target", "Конвертировать в"), item.ConvertTarget);
        var prev = f.Text(L("Prev proxy (remarks)", "Пред. прокси (имя)"), item.PrevProfile);
        var next = f.Text(L("Next proxy (remarks)", "След. прокси (имя)"), item.NextProfile);
        var memo = f.Text(L("Memo", "Заметка"), item.Memo);
        f.Note(L("Servers of this subscription are replaced on every update.", "Серверы подписки заменяются при каждом обновлении."));

        var minInterval = AppHost.Settings.SubUpdateMinIntervalMinutes;
        f.Validate = () =>
        {
            if (remarks.Text.Trim().IsNullOrEmpty() || url.Text.Trim().IsNullOrEmpty())
            {
                return L("Name and URL are required", "Нужны имя и URL");
            }
            var iv = Form.Int(interval, -1);
            return iv == 0 || iv >= minInterval
                ? null
                : L($"Auto update: 0 (off) or at least {minInterval} min", $"Автообновление: 0 (выкл) или не меньше {minInterval} мин");
        };
        if (!f.Run(app))
        {
            return false;
        }

        item.Remarks = remarks.Text.Trim();
        item.Url = url.Text.Trim();
        item.MoreUrl = more.Text.Trim();
        item.Enabled = Form.Bool(enabled);
        item.AutoUpdateInterval = Math.Max(0, Form.Int(interval, 0));
        item.Filter = filter.Text.Trim();
        item.UserAgent = ua.Text.Trim();
        item.ConvertTarget = convert.Text.Trim();
        item.PrevProfile = prev.Text.Trim();
        item.NextProfile = next.Text.Trim();
        item.Memo = memo.Text.Trim();
        return true;
    }
}

internal static class SettingsDialogs
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    /// <summary>Manual tests and the test engine (shared by manual and background tests).</summary>
    public static bool Tests(IApplication app)
    {
        var s = AppHost.Settings;
        using var f = new Form(L("Tests", "Тесты"), 36);
        f.Section(L("Parallel test engine (manual and background)", "Параллельный движок (ручные и фоновые тесты)"));
        var batch = f.Number(L("Servers per core process", "Серверов на процесс ядра"), s.BatchSize);
        var cores = f.Number(L("Core processes at once", "Процессов ядра одновременно"), s.ParallelCores);
        var pings = f.Number(L("Parallel pings", "Параллельных пингов"), s.PingConcurrency);

        f.Section(L("Manual tests", "Ручные тесты"));
        var speeds = f.Number(L("Speed tests at once", "Замеров скорости одновременно"), s.SpeedConcurrency);
        var top = f.Number(L("`m`: speed of top-N (0 = all)", "«m»: скорость топ-N (0 = все)"), s.SpeedTopN);
        var sort = f.Check(L("Sort the list after a test", "Сортировать список после теста"), s.SortAfterTest);
        f.Note(L("Speed test duration and URLs: F2 → Test URLs & timeouts.", "Длительность замера и URL: F2 → URL и таймауты тестов."));

        f.Section(L("Exit IP / country", "Выходной IP / страна"));
        var ip = f.Check(L("Tests: query exit IP / country", "Тесты: определять выходной IP / страну"), s.QueryIpInfo);
        var geoOnConnect = f.Check(L("On connect: check IP / country vs name", "При подключении: сверять IP / страну с названием"), s.CheckCountryOnConnect);

        f.Section(L("Journal", "Журнал"));
        var coreOut = f.Check(L("Show core output in the journal", "Показывать вывод ядер в журнале"), s.ShowCoreOutput);

        if (!f.Run(app))
        {
            return false;
        }
        s.BatchSize = Form.Int(batch, s.BatchSize);
        s.ParallelCores = Form.Int(cores, s.ParallelCores);
        s.PingConcurrency = Form.Int(pings, s.PingConcurrency);
        s.SpeedConcurrency = Form.Int(speeds, s.SpeedConcurrency);
        s.SpeedTopN = Form.Int(top, s.SpeedTopN);
        s.SortAfterTest = Form.Bool(sort);
        s.QueryIpInfo = Form.Bool(ip);
        s.CheckCountryOnConnect = Form.Bool(geoOnConnect);
        s.ShowCoreOutput = Form.Bool(coreOut);
        AppHost.SaveSettings();
        return true;
    }

    /// <summary>Scheduler, subscription rate limit and auto switching (the Alive group relies on them).</summary>
    public static bool Background(IApplication app, IReadOnlyList<SubItem> subs)
    {
        var s = AppHost.Settings;
        var aliveOn = s.AliveEnabled;
        // The Alive group is a result of the cycle, never its source.
        var sources = subs.Where(x => x.Id != AliveGroup.CurrentId).ToList();
        var scopeNames = new List<string> { L("All servers", "Все серверы") };
        scopeNames.AddRange(sources.Select(x => x.Remarks));
        int ScopeIndex(string id) => id.IsNullOrEmpty() ? 0 : Math.Max(0, sources.FindIndex(x => x.Id == id) + 1);
        string ScopeId(int i) => i <= 0 || i > sources.Count ? "" : sources[i - 1].Id;

        var switchNames = new List<string> { aliveOn ? $"{s.AliveName} ({L("default", "по умолчанию")})" : L("All tested", "Все протестированные") };
        switchNames.AddRange(sources.Select(x => x.Remarks));

        using var f = new Form(L("Background", "Фоновый режим"), 36);
        f.Section(L("Schedule", "Расписание"));
        var bgOn = f.Check(L("Enabled", "Включён"), s.BackgroundEnabled);
        var interval = f.Number(L("Interval after a cycle ends, min", "Пауза между циклами, мин"), s.BackgroundIntervalMinutes);
        var scope = f.Picker(L("Test servers of", "Тестировать серверы"), scopeNames, ScopeIndex(s.BackgroundSubId));
        TextField? topSpeeds = null;
        OptionSelector<TestMode>? mode = null;
        CheckBox? upd = null;
        if (aliveOn)
        {
            f.Note(L($"The {s.AliveName} group is on: each cycle updates due subscriptions, pings every server\nand measures the speed of every one that answered (mode and top-N do not apply).",
                     $"Группа {s.AliveName} включена: каждый цикл обновляет подписки (если пора), пингует все серверы\nи меряет скорость каждого ответившего (режим и топ-N не применяются)."));
        }
        else
        {
            mode = f.Options(L("Mode", "Режим"), s.BackgroundMode);
            upd = f.Check(L("Update subscriptions first", "Сначала обновлять подписки"), s.UpdateSubsBeforeTest);
        }
        var afterUpd = f.Check(L("Test after a subscription update", "Тестировать после обновления подписки"), s.TestAfterSubUpdate);

        f.Section(L("Speed tests in background", "Замер скорости в фоне"));
        var bgSpeeds = f.Number(L("Speed tests at once", "Замеров одновременно"), s.BackgroundSpeedConcurrency);
        var bgSeconds = f.Number(L("Duration of one test, s", "Длительность одного замера, с"), s.BackgroundSpeedTestSeconds);
        f.Note(L("1 at a time: each server gets the whole link, results are comparable.", "По одному: каждый сервер получает весь канал, результаты сравнимы."));

        f.Section(L("Subscriptions", "Подписки"));
        var subLimit = f.Number(L("Update not more often than, min", "Обновлять не чаще, чем раз в, мин"), s.SubUpdateMinIntervalMinutes);

        f.Section(L("Auto switch", "Автопереключение"));
        var sw = f.Options(L("Mode", "Режим"), s.AutoSwitch);
        var thr = f.Number(L("Fastest: if faster by, %", "Fastest: если быстрее на, %"), s.SwitchThresholdPercent);
        var swScope = f.Picker(L("Switch only within", "Переключать только в"), switchNames, ScopeIndex(s.AutoSwitchSubId));
        f.Note(L("Off: the active server is never changed automatically.\nFailover: only when the active server is dead. Fastest: also when another one is faster.",
                 "Off — активный сервер сам не меняется.\nFailover — только если активный умер. Fastest — ещё и если другой быстрее на порог."));

        f.Validate = () => aliveOn && !Form.Bool(bgOn)
            ? L($"The {s.AliveName} group needs the background mode: turn the group off first (F2 → {s.AliveName}).",
                $"Группе {s.AliveName} нужен фоновый режим: сначала выключите группу (F2 → {s.AliveName}).")
            : null;
        if (!f.Run(app))
        {
            return false;
        }
        s.BackgroundEnabled = Form.Bool(bgOn);
        s.BackgroundIntervalMinutes = Form.Int(interval, s.BackgroundIntervalMinutes);
        s.BackgroundSubId = ScopeId(scope());
        if (mode != null)
        {
            s.BackgroundMode = mode.Value ?? s.BackgroundMode;
        }
        if (upd != null)
        {
            s.UpdateSubsBeforeTest = Form.Bool(upd);
        }
        s.TestAfterSubUpdate = Form.Bool(afterUpd);
        s.BackgroundSpeedConcurrency = Form.Int(bgSpeeds, s.BackgroundSpeedConcurrency);
        s.BackgroundSpeedTestSeconds = Form.Int(bgSeconds, s.BackgroundSpeedTestSeconds);
        var oldLimit = s.SubUpdateMinIntervalMinutes;
        s.SubUpdateMinIntervalMinutes = Form.Int(subLimit, s.SubUpdateMinIntervalMinutes);
        s.AutoSwitch = sw.Value ?? s.AutoSwitch;
        s.SwitchThresholdPercent = Form.Int(thr, s.SwitchThresholdPercent);
        s.AutoSwitchSubId = ScopeId(swScope());
        AppHost.SaveSettings();
        if (s.SubUpdateMinIntervalMinutes != oldLimit)
        {
            _ = ProxyController.EnforceAutoUpdateLimitAsync();
        }
        return true;
    }

    public static bool Proxy(IApplication app)
    {
        var config = AppHost.Config;
        var inbound = config.Inbound.First();
        var logLevels = new List<string> { "debug", "info", "warning", "error", "none" };

        using var f = new Form(L("Local proxy", "Локальный прокси"), 30);
        var port = f.Number(L("Local port (socks+http)", "Локальный порт (socks+http)"), inbound.LocalPort);
        var second = f.Check(L("Also open an extra port = local port + 1", "Дополнительно открыть порт = локальный + 1"), inbound.SecondLocalPortEnabled);
        var lan = f.Check(L("Allow LAN connections", "Разрешить подключения из LAN"), inbound.AllowLANConn);
        var lanPort = f.Check(L("Separate port for LAN", "Отдельный порт для LAN"), inbound.NewPort4LAN);
        var user = f.Text(L("LAN auth user", "LAN: пользователь"), inbound.User);
        var pass = f.Text(L("LAN auth password", "LAN: пароль"), inbound.Pass, secret: true);
        var udp = f.Check(L("UDP", "UDP"), inbound.UdpEnabled);
        var sniff = f.Check(L("Sniffing", "Sniffing"), inbound.SniffingEnabled);
        var routeOnly = f.Check(L("Sniffing: route only", "Sniffing: только маршрутизация"), inbound.RouteOnly);
        var logLevel = f.Picker(L("Core log level", "Уровень лога ядра"), logLevels, Math.Max(0, logLevels.IndexOf(config.CoreBasicItem.Loglevel)));
        var tunSingBox = f.Check(L("TUN via sing-box (as v2rayN GUI ≤ 7.20)", "TUN через sing-box (как GUI v2rayN ≤ 7.20)"), AppHost.Settings.TunViaSingBox);
        var coreLog = f.Check(L("Write core access/error logs", "Писать логи ядра в файлы"), config.CoreBasicItem.LogEnabled);
        var except = f.Text(L("System proxy exceptions", "Исключения системного прокси"), config.SystemProxyItem.SystemProxyExceptions);
        f.Note(L("The core also uses local port +1…+6 and +21 and up (tests). Next to v2rayN GUI use e.g. 10908.\n" +
                 "Other v2rayN options live in guiConfigs/guiNConfig.json (edit while the TUI is closed).",
                 "Ядро занимает также порты локальный+1…+6 и от +21 (тесты). Рядом с GUI v2rayN берите, например, 10908.\n" +
                 "Остальные опции v2rayN — в guiConfigs/guiNConfig.json (редактируйте при закрытом TUI)."));

        if (!f.Run(app))
        {
            return false;
        }
        inbound.LocalPort = Math.Clamp(Form.Int(port, inbound.LocalPort), 1, 65535);
        inbound.SecondLocalPortEnabled = Form.Bool(second);
        inbound.AllowLANConn = Form.Bool(lan);
        inbound.NewPort4LAN = Form.Bool(lanPort);
        inbound.User = user.Text.Trim();
        inbound.Pass = pass.Text;
        inbound.UdpEnabled = Form.Bool(udp);
        inbound.SniffingEnabled = Form.Bool(sniff);
        inbound.RouteOnly = Form.Bool(routeOnly);
        config.CoreBasicItem.Loglevel = logLevels[logLevel()];
        config.CoreBasicItem.LogEnabled = Form.Bool(coreLog);
        AppHost.Settings.TunViaSingBox = Form.Bool(tunSingBox);
        AppHost.SaveSettings();
        config.SystemProxyItem.SystemProxyExceptions = except.Text.Trim();
        AppManager.Instance.Reset();
        _ = ConfigHandler.SaveConfig(config);
        return true;
    }

    public static bool Alive(IApplication app)
    {
        var s = AppHost.Settings;
        using var f = new Form(L("Alive group", "Группа Alive"), 30, 104);
        var on = f.Check(L("Maintain the group", "Вести группу"), s.AliveEnabled);
        var name = f.Text(L("Group name", "Название группы"), s.AliveName);
        var minSpeed = f.Text(L("Min speed, MB/s", "Мин. скорость, МБ/с"), s.AliveMinSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var maxDelay = f.Number(L("Max delay, ms (0 = any)", "Макс. задержка, мс (0 = любая)"), s.AliveMaxDelay);
        f.Note(L(
            "Each background cycle: update subscriptions (if due) → ping every server → measure the speed\n" +
            "of every server that answered → rebuild the group (add new, drop dead/slow, keep the rest).\n" +
            "Subscription updates never touch the group.\n" +
            "Cycle interval, speed test settings, subscription rate limit and auto switch: F2 → Background.",
            "Каждый фоновый цикл: обновить подписки (если пора) → пинг всех серверов → скорость всех\n" +
            "ответивших → пересобрать группу (новые добавить, мёртвые/медленные убрать, остальные не трогать).\n" +
            "Обновление подписок группу не затрагивает.\n" +
            "Интервал циклов, замер скорости, частота обновления подписок и автопереключение: F2 → Фоновый режим."));

        static decimal? ParseSpeed(string text) =>
            decimal.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

        f.Validate = () => ParseSpeed(minSpeed.Text) is null ? L("Speed must be a number, e.g. 1.5", "Скорость — число, например 1,5") : null;
        if (!f.Run(app))
        {
            return false;
        }
        s.AliveEnabled = Form.Bool(on);
        s.AliveName = name.Text.Trim();
        s.AliveMinSpeed = ParseSpeed(minSpeed.Text) ?? s.AliveMinSpeed;
        s.AliveMaxDelay = Form.Int(maxDelay, s.AliveMaxDelay);
        if (s.AliveEnabled)
        {
            // The group is maintained by the background cycles.
            s.BackgroundEnabled = true;
        }
        AppHost.SaveSettings();
        return true;
    }

    // Presets of v2rayN plus resolvers that work from Russia (its lists are China-oriented).
    private static readonly string[] DirectDnsPresets =
        ["77.88.8.8", "77.88.8.1", "https://common.dot.dns.yandex.net/dns-query", "localhost", .. Global.DomainDirectDNSAddress];

    private static readonly string[] RemoteDnsPresets =
        ["8.8.8.8,https://dns.google/dns-query,1.1.1.1", .. Global.DomainRemoteDNSAddress];

    private static readonly string[] BootstrapDnsPresets = ["77.88.8.8", "8.8.8.8", "1.1.1.1", .. Global.DomainPureIPDNSAddress];

    private static List<string> Distinct(IEnumerable<string> items) => items.Distinct().ToList();

    /// <summary>
    /// v2rayN "DNS settings", basic part (SimpleDNSItem): used for both cores unless a custom DNS
    /// config is enabled for the core (<see cref="DnsCustom"/>).
    /// </summary>
    /// <param name="editCustom">Opens the custom config of a core; returns whether it is enabled now.</param>
    public static bool Dns(IApplication app, bool rayCustom, bool sboxCustom, Func<IApplication, ECoreType, bool> editCustom)
    {
        var d = AppHost.Config.SimpleDNSItem;
        using var f = new Form("DNS", 34, 110);
        if (rayCustom || sboxCustom)
        {
            var which = rayCustom && sboxCustom ? "Xray, sing-box" : rayCustom ? "Xray" : "sing-box";
            f.Note(L($"⚠ A custom DNS config is on for {which}: the settings below do not apply to it.",
                     $"⚠ Для {which} включён пользовательский DNS-конфиг: настройки ниже к нему не применяются."));
        }
        f.Section(L("Servers", "Серверы"));
        var direct = f.Combo(L("Direct DNS", "DNS для прямых подключений"), d.DirectDNS, Distinct(DirectDnsPresets));
        var remote = f.Combo(L("Remote DNS (via proxy)", "Удалённый DNS (через прокси)"), d.RemoteDNS, Distinct(RemoteDnsPresets));
        var boot = f.Combo(L("Bootstrap DNS (IP only)", "Bootstrap DNS (только IP)"), d.BootstrapDNS, Distinct(BootstrapDnsPresets));
        f.Note(L("Several servers: comma-separated. Bootstrap resolves the names of DoH/DoT servers above.",
                 "Несколько серверов — через запятую. Bootstrap разрешает имена DoH/DoT-серверов выше."));

        f.Section(L("Resolution strategy", "Стратегия разрешения"));
        var sFree = f.Choice(L("Direct targets", "Прямые соединения"), Global.DomainStrategy, d.Strategy4Freedom);
        var sProxy = f.Choice(L("Proxied targets", "Через прокси"), Global.DomainStrategy, d.Strategy4Proxy);
        var sDial = f.Choice(L("Proxy server address", "Адрес прокси-сервера"), Global.DomainStrategy, d.Strategy4ProxyDial);
        f.Note(L("Not set / AsIs: system DNS / the remote server resolves. \"Proxy server address\" may cause loops.",
                 "Не задано / AsIs: системный DNS / разрешает удалённый сервер. «Адрес прокси-сервера» может дать петлю."));

        f.Section(L("Options", "Параметры"));
        var fake = f.Check(L("FakeIP (global; filtering only in sing-box)", "FakeIP (глобально; фильтрация только в sing-box)"), d.FakeIP ?? false);
        var fakeRange = f.Combo(L("FakeIP range", "Диапазон FakeIP"), d.FakeIPRange, Global.FakeIPRanges);
        var blockSvcb = f.Check(L("Block SVCB/HTTPS queries (ECH, HTTP/3; always in Xray)", "Блокировать запросы SVCB/HTTPS (ECH, HTTP/3; в Xray всегда)"), d.BlockBindingQuery ?? false);
        var blockAaaa = f.Check(L("Block AAAA (IPv6) queries", "Блокировать запросы AAAA (IPv6)"), d.BlockAAAAQuery ?? false);
        var sysHosts = f.Check(L("Use the system hosts file", "Использовать системный файл hosts"), d.UseSystemHosts ?? false);
        var commonHosts = f.Check(L("Add common DNS hosts", "Добавить стандартные записи hosts"), d.AddCommonHosts ?? false);
        var parallel = f.Check(L("Parallel queries", "Параллельные запросы"), d.ParallelQuery ?? false);
        var stale = f.Check(L("Serve stale records", "Отдавать устаревшие записи (serve stale)"), d.ServeStale ?? false);
        var happy = f.Check(L("Happy Eyeballs (needs UseIP strategy)", "Happy Eyeballs (нужна стратегия UseIP)"), d.EnableHappyEyeballs ?? false);
        var expected = f.Combo(L("Expected IPs (e.g. geoip:ru)", "Ожидаемые IP (напр. geoip:ru)"), d.DirectExpectedIPs, Global.ExpectedIPs);
        var hosts = f.MultiText(L("Hosts: domain ip1 ip2", "Hosts: домен ip1 ip2"), d.Hosts, 3);
        static string Label(string core, bool on) => L($"{core}… ({(on ? "on" : "off")})", $"{core}… ({(on ? "вкл" : "выкл")})");
        f.Actions(L("Custom JSON (replaces this)", "Свой JSON (вместо этого)"),
            (Label("Xray", rayCustom), a => Label("Xray", editCustom(a, ECoreType.Xray))),
            (Label("sing-box", sboxCustom), a => Label("sing-box", editCustom(a, ECoreType.sing_box))));

        f.Validate = () =>
        {
            if (direct.Text.Trim().IsNullOrEmpty() || remote.Text.Trim().IsNullOrEmpty())
            {
                return L("Direct and remote DNS must not be empty.", "DNS для прямых и удалённый DNS не должны быть пустыми.");
            }
            var b = boot.Text.Trim();
            if (b.IsNotEmpty() && b != "localhost" && Utils.String2List(b)?.Any(x => !System.Net.IPAddress.TryParse(x.Trim(), out _)) == true)
            {
                return L("Bootstrap DNS: IP addresses only.", "Bootstrap DNS — только IP-адреса.");
            }
            if (Form.Bool(fake) && !System.Net.IPNetwork.TryParse(fakeRange.Text.Trim(), out _))
            {
                return L("FakeIP range: a CIDR, e.g. 198.18.0.0/15.", "Диапазон FakeIP — CIDR, например 198.18.0.0/15.");
            }
            return null;
        };
        if (!f.Run(app))
        {
            return false;
        }
        d.DirectDNS = direct.Text.Trim();
        d.RemoteDNS = remote.Text.Trim();
        d.BootstrapDNS = boot.Text.Trim();
        d.Strategy4Freedom = sFree();
        d.Strategy4Proxy = sProxy();
        d.Strategy4ProxyDial = sDial();
        d.FakeIP = Form.Bool(fake);
        d.FakeIPRange = fakeRange.Text.Trim();
        d.BlockBindingQuery = Form.Bool(blockSvcb);
        d.BlockAAAAQuery = Form.Bool(blockAaaa);
        d.UseSystemHosts = Form.Bool(sysHosts);
        d.AddCommonHosts = Form.Bool(commonHosts);
        d.ParallelQuery = Form.Bool(parallel);
        d.ServeStale = Form.Bool(stale);
        d.EnableHappyEyeballs = Form.Bool(happy);
        d.DirectExpectedIPs = expected.Text.Trim();
        d.Hosts = hosts.Text.Replace("\r", "").Trim();
        return true;
    }

    /// <summary>v2rayN's "custom DNS" for one core: its own JSON "dns" object instead of the basic settings.</summary>
    public static bool DnsCustom(IApplication app, DNSItem item)
    {
        var ray = item.CoreType == ECoreType.Xray;
        using var f = new Form(L($"Custom DNS — {(ray ? "Xray" : "sing-box")}", $"Свой DNS — {(ray ? "Xray" : "sing-box")}"), 30, 110);
        var on = f.Check(L("Use this config instead of the basic settings", "Использовать этот конфиг вместо основных настроек"), item.Enabled);
        CheckBox? sysHosts = ray ? f.Check(L("Use the system hosts file", "Использовать системный файл hosts"), item.UseSystemHosts) : null;
        var strategy = f.Choice(ray ? L("Direct outbound strategy", "Стратегия прямого выхода") : L("Outbound strategy", "Стратегия выхода"),
            ray ? Global.DomainStrategy : Global.DomainStrategies4Sbox, item.DomainStrategy4Freedom);
        var addr = f.Combo(L("Outbound DNS address", "Исходящий DNS-адрес"), item.DomainDNSAddress, Distinct(BootstrapDnsPresets));
        var normal = f.MultiText(L("DNS object (JSON)", "Объект dns (JSON)"), item.NormalDNS, 9);
        var tun = f.MultiText(L("DNS object in TUN mode", "Объект dns в режиме TUN"), item.TunDNS, 6);
        f.Action(L("Load the default config", "Загрузить стандартный конфиг"), _ =>
        {
            normal.Text = EmbedUtils.GetEmbedText(ray ? Global.DNSV2rayNormalFileName : Global.DNSSingboxNormalFileName);
            tun.Text = EmbedUtils.GetEmbedText(ray ? Global.DNSV2rayNormalFileName : Global.TunSingboxDNSFileName);
        });
        f.Note(ray
            ? L("  Format: https://xtls.github.io/config/dns.html", "  Формат: https://xtls.github.io/config/dns.html")
            : L("  Format: https://sing-box.sagernet.org/configuration/dns/", "  Формат: https://sing-box.sagernet.org/configuration/dns/"));

        string? Check(string text)
        {
            text = text.Trim();
            if (text.IsNullOrEmpty())
            {
                return null;
            }
            if (ray)
            {
                return JsonUtils.ParseJson(text) is System.Text.Json.Nodes.JsonObject o && o["servers"] != null
                    ? null
                    : L("Xray: a JSON object with \"servers\" is expected.", "Xray: нужен JSON-объект с полем \"servers\".");
            }
            var sb = JsonUtils.Deserialize<Dns4Sbox>(text);
            return sb?.servers is { Count: > 0 } servers && servers.All(x => x.type.IsNotEmpty())
                ? null
                : L("sing-box: \"servers\" with a \"type\" for each server is expected.", "sing-box: нужно поле \"servers\", у каждого сервера — \"type\".");
        }
        f.Validate = () =>
        {
            if (Form.Bool(on) && normal.Text.Trim().IsNullOrEmpty())
            {
                return L("The DNS object is empty: load the default config or turn this off.", "Объект dns пуст: загрузите стандартный конфиг или выключите.");
            }
            return Check(normal.Text) ?? Check(tun.Text);
        };
        if (!f.Run(app))
        {
            return false;
        }
        item.Enabled = Form.Bool(on);
        if (sysHosts != null)
        {
            item.UseSystemHosts = Form.Bool(sysHosts);
        }
        item.DomainStrategy4Freedom = strategy();
        item.DomainDNSAddress = addr.Text.Trim();
        var n = normal.Text.Trim();
        var t = tun.Text.Trim();
        // As v2rayN saves it: sing-box configs normalized through its model.
        item.NormalDNS = ray || n.IsNullOrEmpty() ? n : JsonUtils.Serialize(JsonUtils.Deserialize<Dns4Sbox>(n));
        item.TunDNS = ray || t.IsNullOrEmpty() ? t : JsonUtils.Serialize(JsonUtils.Deserialize<Dns4Sbox>(t));
        return true;
    }

    public static bool TestUrls(IApplication app)
    {
        var st = AppHost.Config.SpeedTestItem;
        using var f = new Form(L("Test URLs & timeouts", "URL и таймауты тестов"), 26);
        var ping = f.Text(L("Real ping URL", "URL для пинга"), st.SpeedPingTestUrl);
        f.Note("  " + string.Join("  ", Global.SpeedPingTestUrls.Take(3)));
        var speed = f.Text(L("Speed test URL", "URL для скорости"), st.SpeedTestUrl);
        f.Note("  " + string.Join("  ", Global.SpeedTestUrls.Take(2)));
        var timeout = f.Number(L("Manual speed test duration, s", "Длительность ручного замера скорости, с"), st.SpeedTestTimeout);
        f.Note(L("Background speed tests have their own duration: F2 → Background.", "У фоновых замеров своя длительность: F2 → Фоновый режим."));
        var ipApi = f.Text(L("IP info API", "API для IP"), st.IPAPIUrl);

        if (!f.Run(app))
        {
            return false;
        }
        st.SpeedPingTestUrl = ping.Text.Trim().IsNullOrEmpty() ? Global.SpeedPingTestUrls.First() : ping.Text.Trim();
        st.SpeedTestUrl = speed.Text.Trim().IsNullOrEmpty() ? Global.SpeedTestUrls.First() : speed.Text.Trim();
        st.SpeedTestTimeout = Math.Clamp(Form.Int(timeout, st.SpeedTestTimeout), 3, 300);
        st.IPAPIUrl = ipApi.Text.Trim();
        _ = ConfigHandler.SaveConfig(AppHost.Config);
        return true;
    }
}
