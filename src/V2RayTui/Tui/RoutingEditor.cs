using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace V2RayTui.Tui;

/// <summary>Helpers shared by the routing editor windows.</summary>
internal static class RoutingOps
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    /// <summary>
    /// Runs short engine calls (SQLite) from a modal window. The work runs on the thread pool, so it never
    /// needs the UI thread and cannot deadlock; the UI blocks for a few milliseconds.
    /// </summary>
    public static T Sync<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    public static void Sync(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    public static List<RulesItem> ParseRules(string? json) =>
        (json.IsNullOrEmpty() ? null : JsonUtils.Deserialize<List<RulesItem>>(json)) ?? [];

    /// <summary>Rules in v2rayN's clipboard / file exchange format (no ids, camelCase, no nulls).</summary>
    public static string ExportRules(IEnumerable<RulesItem> rules)
    {
        var list = rules.Select(r =>
        {
            var c = JsonUtils.DeepCopy(r)!;
            c.Id = null;
            return c;
        }).ToList();
        return JsonUtils.Serialize(list, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    public static string Summary(RulesItem r)
    {
        var parts = new List<string>();
        void Add(string tag, List<string>? items)
        {
            if (items is { Count: > 0 })
            {
                var shown = string.Join(",", items.Take(3));
                parts.Add(items.Count > 3 ? $"{tag}{shown} +{items.Count - 3}" : $"{tag}{shown}");
            }
        }
        Add("", r.Domain);
        Add("ip:", r.Ip);
        Add(L("app:", "прил:"), r.Process);
        return string.Join("  ", parts);
    }

    public static string OutboundName(string? tag) => tag switch
    {
        Global.ProxyTag => L("proxy", "прокси"),
        Global.DirectTag => L("direct", "напрямую"),
        Global.BlockTag => L("block", "блок"),
        null or "" => "-",
        _ => "→ " + tag,
    };

    /// <summary>proxy / direct / block, or a specific server (v2rayN routes to a server by its name).</summary>
    public static string? PickOutbound(IApplication app, string current)
    {
        var names = new List<string>
        {
            $"{Global.ProxyTag} — {L("through the active server", "через активный сервер")}",
            $"{Global.DirectTag} — {L("bypass the proxy", "в обход прокси")}",
            $"{Global.BlockTag} — {L("drop", "заблокировать")}",
            L("A specific server…", "Конкретный сервер…"),
        };
        var idx = Global.OutboundTags.IndexOf(current);
        var sel = Dialogs.Choose(app, "Outbound", names, idx < 0 ? 3 : idx);
        if (sel is null)
        {
            return null;
        }
        if (sel < 3)
        {
            return Global.OutboundTags[sel.Value];
        }
        var profiles = Sync(async () => await AppManager.Instance.ProfileItems("") ?? [])
            .Where(p => p.ConfigType != EConfigType.Custom && p.Remarks.IsNotEmpty())
            .ToList();
        if (profiles.Count == 0)
        {
            return null;
        }
        var labels = profiles.Select(p => $"{p.Remarks}  [{p.ConfigType}]").ToList();
        var pi = Dialogs.Choose(app, L("Server", "Сервер"), labels, Math.Max(0, profiles.FindIndex(p => p.Remarks == current)));
        return pi is { } i ? profiles[i].Remarks : null;
    }
}

/// <summary>Running processes (for per-application rules).</summary>
internal static class ProcessPicker
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    public static List<(string Name, string Path)> List()
    {
        var result = new Dictionary<string, string>();
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _))
                {
                    continue;
                }
                try
                {
                    string? path = null;
                    try
                    {
                        path = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
                    }
                    catch
                    {
                        // other users' processes
                    }
                    if (path.IsNullOrEmpty())
                    {
                        var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0')[0];
                        if (cmd.IsNullOrEmpty())
                        {
                            continue; // kernel thread
                        }
                        path = cmd;
                    }
                    path = path.Replace(" (deleted)", "");
                    var name = Path.GetFileName(path);
                    // Skip process titles that are not executable names ("(sd-pam)", "@dbus-daemon", "avahi-daemon: running …").
                    if (name.IsNotEmpty() && name[0] is not ('(' or '@' or '[') && !name.Contains(':') && !name.Any(char.IsWhiteSpace))
                    {
                        result.TryAdd(name, path);
                    }
                }
                catch
                {
                    // vanished
                }
            }
        }
        else
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = p.MainModule?.FileName ?? p.ProcessName;
                    result.TryAdd(Path.GetFileName(path), path);
                }
                catch
                {
                    result.TryAdd(p.ProcessName, p.ProcessName);
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return result.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Multi-select (Space marks); returns process names.</summary>
    public static List<string> Pick(IApplication app)
    {
        var procs = List();
        var width = Math.Min(app.Screen.Width - 4, 110);
        using var dlg = new Dialog { BorderStyle = LineStyle.Rounded, Title = L("Running applications (Space — mark)", "Запущенные приложения (Space — отметить)"), Width = width };
        var items = new ObservableCollection<string>(procs.Select(p => $"{p.Name,-28} {p.Path}"));
        var lv = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Math.Max(5, app.Screen.Height - 12),
            ShowMarks = true,
            MarkMultiple = true,
        };
        lv.SetSource(items);
        if (items.Count > 0)
        {
            lv.SelectedItem = 0; // so the first Space marks immediately
        }
        dlg.Add(lv, new Label
        {
            X = 1,
            Y = Pos.Bottom(lv),
            Text = L("By name matches the app anywhere; to match one binary use its full path (edit the rule).",
                "Имя совпадает с приложением где угодно; для конкретного файла впишите полный путь в правило."),
        });
        dlg.AddButton(new Button { Title = L("_Cancel", "_Отмена") });
        dlg.AddButton(new Button { Title = L("_Add", "_Добавить") });
        lv.SetFocus();
        app.Run(dlg);
        if (dlg.Canceled)
        {
            return [];
        }
        var marked = lv.GetAllMarkedItems().ToList();
        if (marked.Count == 0 && lv.SelectedItem is { } cur)
        {
            marked.Add(cur);
        }
        return marked.Where(i => i >= 0 && i < procs.Count).Select(i => procs[i].Name).ToList();
    }
}

/// <summary>Edits one rule (v2rayN's "Routing rule details").</summary>
internal static class RuleEditor
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    public static bool Run(IApplication app, RulesItem rule)
    {
        using var f = new Form(L("Rule", "Правило"), 20, 110);
        var remarks = f.Text(L("Remarks", "Описание"), rule.Remarks);
        var enabled = f.Check(L("Enabled", "Включено"), rule.Enabled);
        var outbound = f.Value(L("Where to send", "Куда направить"), rule.OutboundTag ?? Global.ProxyTag, RoutingOps.PickOutbound);
        var ruleTypes = Enum.GetNames<ERuleType>().ToList();
        var ruleType = f.Picker(L("Applies to", "Применять к"), ruleTypes, ruleTypes.IndexOf((rule.RuleType ?? ERuleType.ALL).ToString()));
        var networks = Global.RuleNetworks.Select(n => n.IsNullOrEmpty() ? L("any", "любая") : n).ToList();
        var network = f.Picker(L("Network", "Сеть"), networks, Math.Max(0, Global.RuleNetworks.IndexOf(rule.Network ?? "")));
        var port = f.Text(L("Port(s)", "Порт(ы)"), rule.Port);
        var protocol = f.Flags(L("Protocol", "Протокол"), Global.RuleProtocols, rule.Protocol);
        var inbound = f.Flags(L("Inbound", "Вход"), Global.InboundTags, rule.InboundTag);
        f.Note(L("Domain: example.com, domain:x.com, full:a.x.com, keyword:x, regexp:…, geosite:category-ads-all",
                 "Домены: example.com, domain:x.com, full:a.x.com, keyword:x, regexp:…, geosite:category-ads-all"));
        var domain = f.MultiText(L("Domains", "Домены"), Utils.List2String(rule.Domain, true), 6);
        f.Note(L("IP: 1.2.3.4, 10.0.0.0/8, geoip:ru, geoip:private", "IP: 1.2.3.4, 10.0.0.0/8, geoip:ru, geoip:private"));
        var ip = f.MultiText("IP", Utils.List2String(rule.Ip, true), 4);
        var process = f.MultiText(L("Applications", "Приложения"), Utils.List2String(rule.Process, true), 4);
        f.Action(L("Add a running application…", "Добавить запущенное приложение…"), a =>
        {
            var picked = ProcessPicker.Pick(a);
            if (picked.Count == 0)
            {
                return;
            }
            var existing = process.Text.TrimEnd();
            process.Text = existing + (existing.IsNullOrEmpty() ? "" : ",\n") + string.Join(",\n", picked);
        });
        f.Note(L("Per-application rules catch all traffic of the app in TUN mode (F7); otherwise only what the app sends to the proxy.",
                 "Правила для приложений ловят весь трафик приложения в режиме TUN (F7), иначе — только то, что оно шлёт в прокси."));
        var sort = f.Check(L("Sort entries", "Сортировать записи"), false);

        List<string>? Parse(string text, bool isProcess)
        {
            var s = isProcess ? Utils.ParseProcess(text) : Utils.Convert2Comma(text);
            return Form.Bool(sort) ? Utils.String2ListSorted(s) : Utils.String2List(s);
        }

        f.Validate = () =>
        {
            var hasRule = Parse(domain.Text, false) is { Count: > 0 }
                || Parse(ip.Text, false) is { Count: > 0 }
                || Parse(process.Text, true) is { Count: > 0 }
                || protocol().Count > 0
                || port.Text.Trim().IsNotEmpty()
                || network() > 0;
            return hasRule ? null : string.Format(ResUI.RoutingRuleDetailRequiredTips, "Network/Port/Protocol/Domain/IP/Process");
        };

        if (!f.Run(app))
        {
            return false;
        }

        rule.Remarks = remarks.Text.Trim();
        rule.Enabled = Form.Bool(enabled);
        rule.OutboundTag = outbound();
        rule.RuleType = Enum.Parse<ERuleType>(ruleTypes[ruleType()]);
        rule.Network = Global.RuleNetworks[network()];
        rule.Port = port.Text.Trim();
        rule.Protocol = protocol() is { Count: > 0 } pr ? pr : null;
        rule.InboundTag = inbound() is { Count: > 0 } ib ? ib : null;
        rule.Domain = Parse(domain.Text, false);
        rule.Ip = Parse(ip.Text, false);
        rule.Process = Parse(process.Text, true);
        rule.Id ??= Utils.GetGuid(false);
        return true;
    }
}

/// <summary>Properties of a rule set (name, rules URL, domain strategies, custom sing-box rule sets).</summary>
internal static class RoutingSetEditor
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    public static bool Run(IApplication app, RoutingItem item)
    {
        using var f = new Form(L("Rule set", "Набор правил"), 30, 100);
        var remarks = f.Text(L("Name", "Название"), item.Remarks);
        var url = f.Text(L("Rules URL (for import)", "URL правил (для импорта)"), item.Url);
        var xs = new List<string> { "" };
        xs.AddRange(Global.DomainStrategies);
        var xStrategy = f.Picker(L("Domain strategy (xray)", "Доменная стратегия (xray)"),
            xs.Select(s => s.IsNullOrEmpty() ? L("(global)", "(общая)") : s).ToList(), Math.Max(0, xs.IndexOf(item.DomainStrategy ?? "")));
        var sStrategy = f.Picker(L("Domain strategy (sing-box)", "Доменная стратегия (sing-box)"),
            Global.DomainStrategies4Sbox.Select(s => s.IsNullOrEmpty() ? L("(global)", "(общая)") : s).ToList(),
            Math.Max(0, Global.DomainStrategies4Sbox.IndexOf(item.DomainStrategy4Singbox ?? "")));
        var ruleset = f.Text(L("sing-box custom rule-set file", "Свой rule-set sing-box (файл)"), item.CustomRulesetPath4Singbox);
        f.Validate = () => remarks.Text.Trim().IsNullOrEmpty() ? ResUI.PleaseFillRemarks : null;
        if (!f.Run(app))
        {
            return false;
        }
        item.Remarks = remarks.Text.Trim();
        item.Url = url.Text.Trim();
        item.DomainStrategy = xs[xStrategy()];
        item.DomainStrategy4Singbox = Global.DomainStrategies4Sbox[sStrategy()];
        item.CustomRulesetPath4Singbox = ruleset.Text.Trim();
        return true;
    }
}

/// <summary>Rules of one set: ordered list, first match wins.</summary>
internal sealed class RulesWindow : KeyedWindow
{
    private readonly RoutingItem _set;
    private List<RulesItem> _rules;
    private readonly TableView _table;
    private bool _dirty;

    public bool Saved { get; private set; }

    public RulesWindow(RoutingItem set)
    {
        _set = set;
        _rules = RoutingOps.ParseRules(set.RuleSet);
        Title = $"{L("Rules", "Правила")}: {set.Remarks}";
        _table = new TableView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            FullRowSelect = true,
            MultiSelect = false,
            CollectionNavigator = null,
        };
        var st = _table.Style;
        st.ShowHorizontalHeaderOverline = false;
        st.ShowVerticalCellLines = false;
        st.ShowVerticalHeaderLines = false;
        st.ExpandLastColumn = true;
        st.GetOrCreateColumnStyle(4).MaxWidth = 60;
        Add(_table);
        HintLine(L(" Enter edit  a add  Del delete  Space on/off  [ ] { } move  c copy JSON  p paste JSON  f file  u from URL  e set props  s save  Esc close",
                   " Enter изменить  a добавить  Del удалить  Space вкл/выкл  [ ] { } порядок  c копия JSON  p вставить JSON  f файл  u по URL  e свойства  s сохранить  Esc закрыть"));
        Refresh();
    }

    private int Row => _table.Value?.SelectedCell.Y ?? -1;

    private RulesItem? Current => Row >= 0 && Row < _rules.Count ? _rules[Row] : null;

    private void Refresh(int? select = null)
    {
        _table.Table = new RulesTableSource(_rules);
        var row = Math.Clamp(select ?? Row, 0, Math.Max(0, _rules.Count - 1));
        if (_rules.Count > 0)
        {
            _table.SetSelection(0, row, false);
            _table.EnsureCursorIsVisible();
        }
        Title = $"{L("Rules", "Правила")}: {_set.Remarks} ({_rules.Count}){(_dirty ? " *" : "")}";
        _table.Update();
    }

    private void Changed(int? select = null)
    {
        _dirty = true;
        Refresh(select);
    }

    protected override bool OnKey(Key key)
    {
        if (key == Key.Esc)
        {
            CloseAsking();
            return true;
        }
        if (key == Key.Enter)
        {
            Edit();
            return true;
        }
        if (key == Key.Delete)
        {
            Delete();
            return true;
        }
        if (key == Key.Space)
        {
            if (Current is { } r)
            {
                r.Enabled = !r.Enabled;
                Changed();
            }
            return true;
        }
        if (key == Key.S.WithCtrl)
        {
            Save();
            return true;
        }
        switch (KeyMap.Char(key))
        {
            case 'a':
                Add();
                return true;
            case '[':
                Move(EMove.Up);
                return true;
            case ']':
                Move(EMove.Down);
                return true;
            case '{':
                Move(EMove.Top);
                return true;
            case '}':
                Move(EMove.Bottom);
                return true;
            case 'c':
                if (Current is { } cur)
                {
                    Clip.Set(App!, RoutingOps.ExportRules([cur]));
                }
                return true;
            case 'C':
                Clip.Set(App!, RoutingOps.ExportRules(_rules));
                return true;
            case 'p':
                ImportJson(Dialogs.MultiLine(App!, L("Import rules", "Импорт правил"), L("JSON array of rules (v2rayN format):", "JSON-массив правил (формат v2rayN):"), Clip.Get(App!) ?? ""));
                return true;
            case 'f':
                ImportFile();
                return true;
            case 'u':
                ImportUrl();
                return true;
            case 'e':
                if (RoutingSetEditor.Run(App!, _set))
                {
                    Changed();
                }
                return true;
            case 's':
                Save();
                return true;
            case 'q':
                CloseAsking();
                return true;
        }
        return false;
    }

    private void Edit()
    {
        if (Current is not { } r)
        {
            return;
        }
        var copy = JsonUtils.DeepCopy(r)!;
        if (RuleEditor.Run(App!, copy))
        {
            _rules[Row] = copy;
            Changed();
        }
    }

    private void Add()
    {
        var rule = new RulesItem { Id = Utils.GetGuid(false), OutboundTag = Global.ProxyTag, Enabled = true };
        if (!RuleEditor.Run(App!, rule))
        {
            return;
        }
        // Rules are matched top to bottom; a new rule goes above the cursor so it takes effect.
        var at = Math.Max(0, Row);
        _rules.Insert(at, rule);
        Changed(at);
    }

    private void Delete()
    {
        if (Current is not { } r)
        {
            return;
        }
        if (Dialogs.Confirm(App!, L("Delete rule", "Удаление правила"), $"{RoutingOps.OutboundName(r.OutboundTag)}  {RoutingOps.Summary(r)}"))
        {
            _rules.Remove(r);
            Changed();
        }
    }

    private void Move(EMove dir)
    {
        if (Current is not { } r)
        {
            return;
        }
        if (RoutingOps.Sync(() => ConfigHandler.MoveRoutingRule(_rules, Row, dir)) == 0)
        {
            Changed(_rules.IndexOf(r));
        }
    }

    private void ImportJson(string? json)
    {
        if (json.IsNullOrEmpty())
        {
            return;
        }
        var list = RoutingOps.ParseRules(json);
        if (list.Count == 0)
        {
            Dialogs.Error(App!, L("Import rules", "Импорт правил"), ResUI.OperationFailed);
            return;
        }
        foreach (var r in list)
        {
            r.Id = Utils.GetGuid(false);
        }
        // Same question as v2rayN: Yes = append, No = replace.
        var answer = MessageBox.Query(App!, L("Import rules", "Импорт правил"),
            L($"{list.Count} rules. Append to the existing ones or replace them?", $"Правил: {list.Count}. Добавить к существующим или заменить?"),
            L("_Cancel", "_Отмена"), L("_Replace", "_Заменить"), L("_Append", "_Добавить"));
        switch (answer)
        {
            case 1:
                _rules = list;
                break;
            case 2:
                _rules.AddRange(list);
                break;
            default:
                return;
        }
        Changed();
    }

    private void ImportFile()
    {
        var path = Dialogs.Prompt(App!, L("Import rules", "Импорт правил"), L("File with a JSON array of rules:", "Файл с JSON-массивом правил:"));
        if (path.IsNullOrEmpty())
        {
            return;
        }
        try
        {
            ImportJson(File.ReadAllText(path.Trim()));
        }
        catch (Exception ex)
        {
            Dialogs.Error(App!, L("Import rules", "Импорт правил"), ex.Message);
        }
    }

    private void ImportUrl()
    {
        if (_set.Url.IsNullOrEmpty())
        {
            Dialogs.Error(App!, L("Import rules", "Импорт правил"), ResUI.MsgNeedUrl + L(" (e — set properties)", " (e — свойства набора)"));
            return;
        }
        var viaProxy = ProxyController.Instance.CoreRunning;
        var json = RoutingOps.Sync(() => new DownloadService().TryDownloadString(_set.Url, viaProxy, ""));
        ImportJson(json);
    }

    private void Save()
    {
        if (_set.Remarks.IsNullOrEmpty())
        {
            Dialogs.Error(App!, L("Rule set", "Набор правил"), ResUI.PleaseFillRemarks);
            return;
        }
        foreach (var r in _rules)
        {
            r.Id = Utils.GetGuid(false);
        }
        _set.RuleNum = _rules.Count;
        _set.RuleSet = JsonUtils.Serialize(_rules, false);
        if (RoutingOps.Sync(() => ConfigHandler.SaveRoutingItem(AppHost.Config, _set)) == 0)
        {
            _dirty = false;
            Saved = true;
            LogBus.Notice($"{L("Saved", "Сохранено")}: {_set.Remarks}");
            Refresh();
        }
        else
        {
            Dialogs.Error(App!, L("Rule set", "Набор правил"), ResUI.OperationFailed);
        }
    }

    private void CloseAsking()
    {
        if (_dirty)
        {
            var r = MessageBox.Query(App!, L("Unsaved changes", "Несохранённые изменения"), L("Save the rule set?", "Сохранить набор правил?"),
                L("_Cancel", "_Отмена"), L("_Discard", "_Не сохранять"), L("_Save", "_Сохранить"));
            if (r is null or 0)
            {
                return;
            }
            if (r == 2)
            {
                Save();
                if (_dirty)
                {
                    return;
                }
            }
        }
        Close();
    }

    private sealed class RulesTableSource(List<RulesItem> rules) : ITableSource
    {
        public string[] ColumnNames { get; } =
        [
            " ", "#", Loc.T("Where", "Куда"), Loc.T("For", "Для"), Loc.T("Match", "Условие"),
            Loc.T("Port", "Порт"), Loc.T("Net", "Сеть"), Loc.T("Proto", "Протокол"), Loc.T("Remarks", "Описание"),
        ];

        public int Columns => ColumnNames.Length;

        public int Rows => rules.Count;

        public object this[int row, int col]
        {
            get
            {
                var r = rules[row];
                return col switch
                {
                    0 => r.Enabled ? "✓" : "·",
                    1 => (row + 1).ToString(),
                    2 => RoutingOps.OutboundName(r.OutboundTag),
                    3 => r.RuleType is null or ERuleType.ALL ? "" : r.RuleType.ToString()!,
                    4 => RoutingOps.Summary(r),
                    5 => r.Port ?? "",
                    6 => r.Network ?? "",
                    7 => string.Join(",", r.Protocol ?? []),
                    8 => r.Remarks ?? "",
                    _ => "",
                };
            }
        }
    }
}

/// <summary>All rule sets (v2rayN's "Routing settings"): choose the active one, edit, add, copy, delete.</summary>
internal sealed class RoutingSetsWindow : KeyedWindow
{
    private readonly ListView _list;
    private readonly Label _strategies;
    private readonly ObservableCollection<string> _items = [];
    private List<RoutingItem> _sets = [];

    /// <summary>True when something that affects the running core changed.</summary>
    public bool Modified { get; private set; }

    public RoutingSetsWindow()
    {
        Title = L("Routing (traffic splitting)", "Маршрутизация (разделение трафика)");
        _strategies = new Label { X = 1, Y = 0, Width = Dim.Fill(), Height = 1 };
        _list = new ListView { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(2) };
        _list.SetSource(_items);
        Add(_strategies, _list);
        HintLine(L(" Enter rules  Space/s make active  a new  c copy  e properties  Del delete  i import built-in sets  1/2 domain strategy xray/sing-box  Esc close",
                   " Enter правила  Space/s сделать активным  a новый  c копия  e свойства  Del удалить  i встроенные наборы  1/2 доменная стратегия xray/sing-box  Esc закрыть"));
        Reload();
    }

    private RoutingItem? Current => _list.SelectedItem is { } i && i >= 0 && i < _sets.Count ? _sets[i] : null;

    private void Reload(string? selectId = null)
    {
        var keep = selectId ?? Current?.Id;
        _sets = RoutingOps.Sync(async () => await AppManager.Instance.RoutingItems() ?? []);
        _items.Clear();
        foreach (var s in _sets)
        {
            var count = s.RuleNum > 0 ? s.RuleNum : RoutingOps.ParseRules(s.RuleSet).Count;
            _items.Add($"{(s.IsActive ? "●" : " ")} {s.Remarks,-40} {count,4} {L("rules", "правил")}{(s.Url.IsNotEmpty() ? "  ⇣ " + s.Url : "")}");
        }
        var idx = _sets.FindIndex(s => s.Id == keep);
        if (idx < 0)
        {
            idx = Math.Max(0, _sets.FindIndex(s => s.IsActive));
        }
        if (_sets.Count > 0)
        {
            _list.SelectedItem = idx;
        }
        var cfg = AppHost.Config.RoutingBasicItem;
        _strategies.Text = $"{L("Domain strategy", "Доменная стратегия")}: xray = {cfg.DomainStrategy}   sing-box = {(cfg.DomainStrategy4Singbox.IsNullOrEmpty() ? "-" : cfg.DomainStrategy4Singbox)}"
                           + $"      {L("Active set is marked ●", "Активный набор отмечен ●")}";
    }

    protected override bool OnKey(Key key)
    {
        if (key == Key.Esc)
        {
            Close();
            return true;
        }
        if (key == Key.Enter)
        {
            OpenRules();
            return true;
        }
        if (key == Key.Space)
        {
            MakeActive();
            return true;
        }
        if (key == Key.Delete)
        {
            Delete();
            return true;
        }
        switch (KeyMap.Char(key))
        {
            case 's':
                MakeActive();
                return true;
            case 'a':
                New();
                return true;
            case 'c':
                Copy();
                return true;
            case 'e':
                Properties();
                return true;
            case 'i':
                ImportBuiltin();
                return true;
            case '1':
                PickStrategy(false);
                return true;
            case '2':
                PickStrategy(true);
                return true;
            case 'q':
                Close();
                return true;
        }
        return false;
    }

    private void OpenRules()
    {
        if (Current is not { } set)
        {
            return;
        }
        var fresh = RoutingOps.Sync(() => AppManager.Instance.GetRoutingItem(set.Id)) ?? set;
        using var w = new RulesWindow(fresh);
        App!.Run(w);
        if (w.Saved)
        {
            Modified |= fresh.IsActive;
            Reload(fresh.Id);
        }
    }

    private void MakeActive()
    {
        if (Current is not { } set || set.IsActive)
        {
            return;
        }
        if (RoutingOps.Sync(() => ConfigHandler.SetDefaultRouting(AppHost.Config, set)) == 0)
        {
            Modified = true;
            LogBus.Write($"{ResUI.TipChangeRouting}: {set.Remarks}");
            Reload(set.Id);
        }
    }

    private void New()
    {
        var item = new RoutingItem { Remarks = "", RuleSet = "[]", Enabled = true };
        if (!RoutingSetEditor.Run(App!, item))
        {
            return;
        }
        item.Sort = _sets.Count > 0 ? _sets.Max(s => s.Sort) + 1 : 1;
        RoutingOps.Sync(() => ConfigHandler.SaveRoutingItem(AppHost.Config, item));
        Reload(item.Id);
        OpenRules();
    }

    private void Copy()
    {
        if (Current is not { } set)
        {
            return;
        }
        var copy = JsonUtils.DeepCopy(set)!;
        copy.Id = "";
        copy.IsActive = false;
        copy.Locked = false;
        copy.Remarks = set.Remarks + L(" (copy)", " (копия)");
        copy.Sort = _sets.Max(s => s.Sort) + 1;
        RoutingOps.Sync(() => ConfigHandler.SaveRoutingItem(AppHost.Config, copy));
        Reload(copy.Id);
    }

    private void Properties()
    {
        if (Current is not { } set)
        {
            return;
        }
        if (RoutingSetEditor.Run(App!, set))
        {
            RoutingOps.Sync(() => ConfigHandler.SaveRoutingItem(AppHost.Config, set));
            Modified |= set.IsActive;
            Reload(set.Id);
        }
    }

    private void Delete()
    {
        if (Current is not { } set)
        {
            return;
        }
        if (_sets.Count <= 1)
        {
            Dialogs.Error(App!, Title, L("The last rule set cannot be deleted", "Последний набор удалить нельзя"));
            return;
        }
        if (!Dialogs.Confirm(App!, L("Delete rule set", "Удаление набора"), L($"Delete \"{set.Remarks}\"?", $"Удалить «{set.Remarks}»?")))
        {
            return;
        }
        RoutingOps.Sync(async () =>
        {
            await ConfigHandler.RemoveRoutingItem(set);
            if (set.IsActive)
            {
                await ConfigHandler.GetDefaultRouting(AppHost.Config); // activates the first remaining set
            }
        });
        Modified |= set.IsActive;
        Reload();
    }

    private void ImportBuiltin()
    {
        if (!Dialogs.Confirm(App!, L("Built-in rule sets", "Встроенные наборы"),
                L("Add v2rayN's built-in advanced rule sets (for the current regional preset)?", "Добавить встроенные расширенные наборы v2rayN (для текущего регионального пресета)?")))
        {
            return;
        }
        RoutingOps.Sync(() => ConfigHandler.InitRouting(AppHost.Config, true));
        Modified = true;
        Reload();
    }

    private void PickStrategy(bool singbox)
    {
        var cfg = AppHost.Config.RoutingBasicItem;
        var options = singbox ? Global.DomainStrategies4Sbox : Global.DomainStrategies;
        var current = singbox ? cfg.DomainStrategy4Singbox ?? "" : cfg.DomainStrategy ?? "";
        var sel = Dialogs.Choose(App!, singbox ? "Domain strategy (sing-box)" : "Domain strategy (xray)",
            options.Select(o => o.IsNullOrEmpty() ? "-" : o).ToList(), Math.Max(0, options.IndexOf(current)));
        if (sel is not { } i)
        {
            return;
        }
        if (singbox)
        {
            cfg.DomainStrategy4Singbox = options[i];
        }
        else
        {
            cfg.DomainStrategy = options[i];
        }
        RoutingOps.Sync(() => ConfigHandler.SaveConfig(AppHost.Config));
        Modified = true;
        Reload();
    }
}
