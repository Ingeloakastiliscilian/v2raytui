using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;

namespace V2RayTui.Tui;

internal sealed partial class MainWindow : Runnable
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    private readonly SegmentBar _header;
    private readonly FrameView _subsFrame;
    private readonly ListView _subsList;
    private readonly FrameView _serversFrame;
    private readonly TableView _table;
    private readonly FrameView _logFrame;
    private readonly ListView _logList;
    private readonly SegmentBar _statusLine;

    private readonly ObservableCollection<string> _subsItems = [];
    private readonly ObservableCollection<string> _logLines = [];
    private readonly ConcurrentQueue<string> _pendingLog = new();

    private List<SubItem> _subs = [];
    private List<ServerRow> _rows = [];
    private volatile Dictionary<string, ServerRow> _rowById = new();
    private readonly HashSet<string> _marked = [];
    private string _filter = "";
    private int _logHeight = 8;
    private volatile bool _dirty;
    private volatile bool _stateDirty = true;
    private string _notice = "";
    private DateTime _noticeAt;
    private bool _started;

    public TuiExit ExitMode { get; private set; } = TuiExit.Quit;

    private static Config Config => AppHost.Config;

    public MainWindow()
    {
        Title = "v2rayN TUI";
        Width = Dim.Fill();
        Height = Dim.Fill();
        SetScheme(Theme.Base);

        _header = new SegmentBar(Theme.Surface) { X = 0, Y = 0 };

        _subsList = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        _subsList.SetSource(_subsItems);
        _subsFrame = new FrameView
        {
            Title = L("Subscriptions", "Подписки"),
            X = 0,
            Y = 1,
            Width = 26,
            Height = Dim.Fill(Dim.Func(_ => _logHeight + 2)),
        };
        _subsFrame.Add(_subsList);
        Theme.Panel(_subsFrame);
        _subsList.RowRender += (_, e) =>
        {
            if (e.Row >= 0 && e.Row < _subIds.Count && e.Row != _subsList.SelectedItem && _subIds[e.Row] == AliveGroup.CurrentId)
            {
                e.RowAttribute = Theme.A(Theme.Yellow, null, TextStyle.Bold);
            }
        };

        _table = new TableView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
            MultiSelect = false,
            CollectionNavigator = null,
            Table = new ServerTableSource([]),
        };
        ConfigureTableStyle();
        _serversFrame = new FrameView
        {
            Title = L("Servers", "Серверы"),
            X = Pos.Right(_subsFrame),
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(Dim.Func(_ => _logHeight + 2)),
        };
        _serversFrame.Add(_table);
        Theme.Panel(_serversFrame);

        _logList = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        _logList.SetSource(_logLines);
        _logFrame = new FrameView
        {
            Title = L("Log", "Журнал"),
            X = 0,
            Y = Pos.Bottom(_subsFrame),
            Width = Dim.Fill(),
            Height = Dim.Func(_ => _logHeight),
            Visible = true,
        };
        _logFrame.Add(_logList);
        Theme.Panel(_logFrame);
        // No selection bar in the log unless it is focused; colour lines by meaning.
        _logList.SetScheme(Theme.Base with { Active = Theme.A(Theme.Sub), Normal = Theme.A(Theme.Sub) });
        _logList.RowRender += (_, e) =>
        {
            if (e.Row < 0 || e.Row >= _logLines.Count || (_logList.HasFocus && e.Row == _logList.SelectedItem))
            {
                return;
            }
            var line = _logLines[e.Row];
            e.RowAttribute = line.Contains('⚠') || line.Contains("Failed") || line.Contains("Не удалось") || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? Theme.A(line.Contains('⚠') ? Theme.Yellow : Theme.Red)
                : line.Contains("[alive] +") ? Theme.A(Theme.Green)
                : line.Contains("[alive] −") ? Theme.A(Theme.Peach)
                : line.Contains("[alive]") || line.Contains("[bg]") ? Theme.A(Theme.Mauve)
                : line.Contains("[test]") ? Theme.A(Theme.Blue)
                : Theme.A(Theme.Sub);
        };

        _statusLine = new SegmentBar(Theme.None) { X = 0, Y = Pos.Bottom(_logFrame) };

        var statusBar = new StatusBar();
        statusBar.SetScheme(Theme.ShortcutBar);
        statusBar.Add(
            Hint("F1", L("Help", "Справка")),
            Hint("Enter", L("Connect", "Подключить")),
            Hint("r/t/s/m", L("Test", "Тест")),
            Hint("Esc", L("Stop", "Стоп")),
            Hint("u/U", L("Update subs", "Обновить подписки")),
            Hint("/", L("Filter", "Фильтр")),
            Hint("F2", L("Settings", "Настройки")),
            Hint("q", L("Quit", "Выход")));

        Add(_header, _subsFrame, _serversFrame, _logFrame, _statusLine, statusBar);

        _subsList.ValueChanged += (_, e) =>
        {
            // Rebuilding the list (Clear/Add) also raises this: only real user moves count.
            if (_started && !_rebuildingSubs && e.NewValue is int i)
            {
                OnSubSelected(i);
            }
        };
        _table.Accepted += (_, _) => ConnectSelected();
    }

    private static Shortcut Hint(string key, string title) => new() { Title = title, Text = key, CanFocus = false };

    #region lifecycle

    protected override void OnIsRunningChanged(bool newIsRunning)
    {
        base.OnIsRunningChanged(newIsRunning);
        if (!newIsRunning || _started)
        {
            return;
        }
        _started = true;

        foreach (var line in LogBus.Snapshot().TakeLast(300))
        {
            _logLines.Add(line);
        }
        LogBus.LineAdded += OnLogLine;
        LogBus.NoticeAdded += OnNotice;
        TestService.Instance.Updated += OnTestUpdate;
        TestService.Instance.JobChanged += OnJobChanged;
        ProxyController.Instance.StateChanged += MarkStateDirty;
        ProxyController.Instance.ServersChanged += OnServersChanged;
        BackgroundScheduler.Instance.Changed += MarkStateDirty;
        App!.Keyboard.KeyDown += OnGlobalKey;
        App!.Paste += OnPaste;
        App!.AddTimeout(TimeSpan.FromMilliseconds(250), Tick);

        _table.SetFocus();
        Fire(StartupAsync);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LogBus.LineAdded -= OnLogLine;
            LogBus.NoticeAdded -= OnNotice;
            TestService.Instance.Updated -= OnTestUpdate;
            TestService.Instance.JobChanged -= OnJobChanged;
            ProxyController.Instance.StateChanged -= MarkStateDirty;
            ProxyController.Instance.ServersChanged -= OnServersChanged;
            BackgroundScheduler.Instance.Changed -= MarkStateDirty;
        }
        base.Dispose(disposing);
    }

    private async Task StartupAsync()
    {
        TestService.Instance.ApplySettings(AppHost.Settings);
        await RefreshRoutingNameAsync();
        await LoadSubsAsync();
        await ReloadServersAsync();

        if (CoreUpdater.MissingComponents() is { Count: > 0 } missing)
        {
            var yes = await OnUi(() => Dialogs.Confirm(App!, L("First run", "Первый запуск"),
                L($"Missing: {string.Join(", ", missing)}.\nDownload now from GitHub?",
                  $"Не хватает: {string.Join(", ", missing)}.\nСкачать сейчас с GitHub?")));
            if (yes)
            {
                await CoreUpdater.InstallMissingAsync(viaProxy: false);
            }
        }

        CoreUpdater.WarnOutdatedCores();
        await EnsureTunAccessAsync();
        if (_rows.Count > 0 || await ConfigHandler.GetDefaultServer(Config) != null)
        {
            await ProxyController.Instance.ReloadAsync();
        }
        else
        {
            LogBus.Notice(L("No servers yet: press `a` in Subscriptions to add one, or paste share links (p).",
                "Серверов пока нет: добавьте подписку (`a` в панели подписок) или вставьте ссылки (p)."));
        }
        BackgroundScheduler.Instance.Start();
        ProxyController.Instance.StartWatchdog();
        MarkStateDirty();
    }

    /// <summary>TUN was left on: get sudo access at start (passwordless rule, or ask the password).</summary>
    private async Task EnsureTunAccessAsync()
    {
        if (!Config.TunModeItem.EnableTun || ProxyController.TunAllowed || Utils.IsWindows()
            || await ProxyController.TryPasswordlessSudoAsync())
        {
            return;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var pwd = await OnUi(() => Dialogs.Prompt(App!, "TUN",
                attempt == 0
                    ? L("TUN is on. sudo password (kept in memory only; Esc — start without TUN):", "TUN включён. Пароль sudo (хранится только в памяти; Esc — без TUN):")
                    : L("Wrong password, try again:", "Неверный пароль, ещё раз:"),
                secret: true));
            if (pwd.IsNullOrEmpty())
            {
                return;
            }
            if (await ProxyController.UseSudoPasswordAsync(pwd))
            {
                return;
            }
        }
    }

    /// <summary>UI refresh pump: coalesces updates from worker threads.</summary>
    private bool Tick()
    {
        var logChanged = false;
        while (_pendingLog.TryDequeue(out var line))
        {
            _logLines.Add(line);
            logChanged = true;
        }
        if (logChanged)
        {
            while (_logLines.Count > 1500)
            {
                _logLines.RemoveAt(0);
            }
            _logList.SelectedItem = _logLines.Count - 1;
        }
        if (_dirty)
        {
            _dirty = false;
            _table.SetNeedsDraw();
        }
        UpdateStatusTexts();
        ReloadIfServersChanged();
        OfferFreePortIfNeeded();
        return true;
    }

    private void OnLogLine(string line) => _pendingLog.Enqueue(line);

    private void OnNotice(string msg)
    {
        _notice = msg;
        _noticeAt = DateTime.Now;
        _stateDirty = true;
    }

    private void MarkStateDirty() => _stateDirty = true;

    private int? _portPrompted;

    /// <summary>Offers a free local port once when another application holds ours.</summary>
    private void OfferFreePortIfNeeded()
    {
        var busy = ProxyController.Instance.PortConflict;
        if (busy is null || busy == _portPrompted || App?.TopRunnableView != this)
        {
            return;
        }
        _portPrompted = busy;
        var free = ProxyController.SuggestFreeBasePort();
        if (free is null)
        {
            return;
        }
        if (Dialogs.Confirm(App!, L("Port is busy", "Порт занят"),
                L($"Local port {busy} is used by another application (v2rayN GUI?),\nso the TUI proxy cannot start.\n\nSwitch the TUI to port {free}? Apps that use the proxy must then use {free}.",
                  $"Локальный порт {busy} занят другим приложением (GUI v2rayN?),\nпоэтому прокси TUI не запускается.\n\nПереключить TUI на порт {free}? Приложениям, использующим прокси, тогда нужен порт {free}.")))
        {
            Fire(() => ProxyController.Instance.ChangeLocalPortAsync(free.Value));
        }
    }

    private void OnJobChanged(TestJob job)
    {
        _stateDirty = true;
        if (!job.IsRunning && !job.Background && AppHost.Settings.SortAfterTest && !job.Cancelled)
        {
            Fire(() => SortByResultAsync(job.Mode is TestMode.Speed or TestMode.PingThenSpeed));
        }
    }

    private volatile bool _serversChanged;
    private DateTime _serversReloadedAt;

    // During a background cycle the list changes every few seconds: coalesce (see Tick).
    private void OnServersChanged() => _serversChanged = true;

    private void ReloadIfServersChanged()
    {
        if (!_serversChanged || (DateTime.Now - _serversReloadedAt).TotalSeconds < 2)
        {
            return;
        }
        _serversChanged = false;
        _serversReloadedAt = DateTime.Now;
        Fire(async () =>
        {
            await LoadSubsAsync();
            await ReloadServersAsync();
        });
    }

    private void OnTestUpdate(TestUpdate u)
    {
        if (!_rowById.TryGetValue(u.IndexId, out var r))
        {
            return;
        }
        if (u.Delay is { } d)
        {
            r.Delay = d;
        }
        if (u.Speed is { } s)
        {
            r.Speed = s;
        }
        if (u.DelayStatus != null)
        {
            r.DelayStatus = u.DelayStatus;
        }
        if (u.SpeedStatus != null)
        {
            r.SpeedStatus = u.SpeedStatus;
        }
        if (u.IpInfo != null)
        {
            r.IpInfo = u.IpInfo;
        }
        _dirty = true;
    }

    #endregion lifecycle

    #region data

    private bool _rebuildingSubs;

    /// <summary>Subscription ids in list order ("" = all servers).</summary>
    private List<string> _subIds = [];

    private async Task LoadSubsAsync()
    {
        var subs = await AppManager.Instance.SubItems() ?? [];
        var counts = (await AppManager.Instance.ProfileItems("") ?? [])
            .GroupBy(p => p.Subid ?? "")
            .ToDictionary(g => g.Key, g => g.Count());
        var aliveId = AliveGroup.CurrentId;
        await OnUi(() =>
        {
            string Line(string name, int count) => $"{(name.Length > 17 ? name[..16] + "…" : name),-18}{count,5}";

            // The Alive group goes first; "All servers" does not include its copies.
            var ordered = subs.Where(s => s.Id == aliveId).Concat(subs.Where(s => s.Id != aliveId)).ToList();
            var ids = new List<string>();
            var lines = new List<string>();
            foreach (var s in ordered.Where(s => s.Id == aliveId))
            {
                ids.Add(s.Id);
                lines.Add(Line("★ " + s.Remarks, counts.GetValueOrDefault(s.Id, 0)));
            }
            ids.Add("");
            lines.Add(Line(L("All servers", "Все серверы"), counts.Where(kv => kv.Key != aliveId).Sum(kv => kv.Value)));
            foreach (var s in ordered.Where(s => s.Id != aliveId))
            {
                ids.Add(s.Id);
                lines.Add(Line((s.Enabled ? "" : "·") + s.Remarks, counts.GetValueOrDefault(s.Id ?? "", 0)));
            }

            _aliveCount = aliveId != null ? counts.GetValueOrDefault(aliveId, 0) : 0;
            _stateDirty = true;
            var current = Config.SubIndexId ?? "";
            if (!ids.Contains(current))
            {
                current = "";
                Config.SubIndexId = "";
            }
            _rebuildingSubs = true;
            try
            {
                _subs = subs;
                _subIds = ids;
                if (!_subsItems.SequenceEqual(lines))
                {
                    _subsItems.Clear();
                    foreach (var l in lines)
                    {
                        _subsItems.Add(l);
                    }
                }
                _subsList.SelectedItem = ids.IndexOf(current);
            }
            finally
            {
                _rebuildingSubs = false;
            }
            return true;
        });
    }

    private void OnSubSelected(int index)
    {
        var id = index >= 0 && index < _subIds.Count ? _subIds[index] : "";
        if (id == (Config.SubIndexId ?? ""))
        {
            return;
        }
        Config.SubIndexId = id;
        _marked.Clear();
        Fire(() => ReloadServersAsync());
    }

    private SubItem? CurrentSub => Config.SubIndexId.IsNullOrEmpty() ? null : _subs.FirstOrDefault(s => s.Id == Config.SubIndexId);

    private async Task ReloadServersAsync(string? selectId = null)
    {
        var rows = await ServerRepository.LoadAsync(Config.SubIndexId, _filter);
        // Keep transient test states of rows that are still being tested.
        var old = _rowById;
        foreach (var r in rows)
        {
            if (old.TryGetValue(r.IndexId, out var o))
            {
                r.DelayStatus = o.DelayStatus;
                if (o.SpeedStatus.IsNotEmpty() && r.Speed <= 0)
                {
                    r.SpeedStatus = o.SpeedStatus;
                }
            }
        }

        await OnUi(() =>
        {
            var keepId = selectId ?? Current?.IndexId ?? Config.IndexId;
            _rows = rows;
            _rowById = rows.ToDictionary(r => r.IndexId);
            _marked.IntersectWith(_rowById.Keys);
            foreach (var r in rows)
            {
                r.Marked = _marked.Contains(r.IndexId);
            }
            _table.Table = new ServerTableSource(rows);
            var idx = rows.FindIndex(r => r.IndexId == keepId);
            if (idx < 0 && rows.Count > 0)
            {
                idx = 0;
            }
            if (idx >= 0)
            {
                _table.SetSelection(0, idx, false);
                _table.EnsureCursorIsVisible();
            }
            UpdateServersTitle();
            _table.Update();
            return true;
        });
    }

    private void UpdateServersTitle()
    {
        var sub = CurrentSub?.Remarks ?? L("All", "Все");
        var marks = _marked.Count > 0 ? $" · {L("marked", "отмечено")} {_marked.Count}" : "";
        var filter = _filter.IsNotEmpty() ? $" · {L("filter", "фильтр")}: {_filter}" : "";
        _serversFrame.Title = $"{L("Servers", "Серверы")} [{sub}] {_rows.Count}{marks}{filter}";
    }

    private ServerRow? Current =>
        _table.Value is { } v && v.SelectedCell.Y >= 0 && v.SelectedCell.Y < _rows.Count ? _rows[v.SelectedCell.Y] : null;

    /// <summary>Marked rows, or the row under the cursor.</summary>
    private List<ServerRow> Selection()
    {
        var marked = _rows.Where(r => r.Marked).ToList();
        if (marked.Count > 0)
        {
            return marked;
        }
        return Current is { } c ? [c] : [];
    }

    /// <summary>Marked rows, or everything visible (what "test" applies to).</summary>
    private List<ServerRow> TestScope()
    {
        var marked = _rows.Where(r => r.Marked).ToList();
        return marked.Count > 0 ? marked : _rows.ToList();
    }

    #endregion data

    #region status texts

    private void UpdateStatusTexts()
    {
        if (!_stateDirty && TestService.Instance.RunningJobs.Count == 0 && (DateTime.Now - _noticeAt).TotalSeconds > 8)
        {
            return;
        }
        _stateDirty = false;
        UpdateHeader();
        UpdateStatusLine();
    }

    private void UpdateHeader()
    {
        var pc = ProxyController.Instance;
        var left = new List<SegmentBar.Segment>
        {
            new(" ◆ v2rayN TUI ", Theme.A(Theme.Crust, Theme.Mauve, TextStyle.Bold)),
            new(" ", Theme.A(Theme.Text)),
        };
        if (pc.CoreRunning)
        {
            left.Add(new($" ● {L("connected", "подключено")} ", Theme.A(Theme.Crust, Theme.Green, TextStyle.Bold)));
        }
        else if (pc.PortConflict is { } busy)
        {
            left.Add(new($" ⚠ {L("port", "порт")} {busy} {L("busy", "занят")} ", Theme.A(Theme.Crust, Theme.Peach, TextStyle.Bold)));
        }
        else
        {
            left.Add(new($" ○ {L("disconnected", "отключено")} ", Theme.A(Theme.Crust, Theme.Red, TextStyle.Bold)));
        }
        if (pc.RunningRemarks.IsNotEmpty())
        {
            left.Add(new("  " + pc.RunningRemarks, Theme.A(Theme.Text, null, TextStyle.Bold)));
        }
        if (pc.Checking)
        {
            left.Add(new("  ⟳", Theme.A(Theme.Blue)));
        }
        else if (pc.LastDelay != 0)
        {
            left.Add(pc.LastDelay > 0
                ? new($"  {pc.LastDelay} {L("ms", "мс")}", Theme.A(Theme.DelayColor(pc.LastDelay)))
                : new($"  ✗ {L("server does not respond", "сервер не отвечает")} ", Theme.A(Theme.Crust, Theme.Red, TextStyle.Bold)));
        }
        if (pc.ExitGeo is { } geo)
        {
            left.Add(new($"  ⇢ {GeoIp.Flag(geo.Country)} {geo.CountryName}", Theme.A(pc.CountryMismatch ? Theme.Yellow : Theme.Teal)));
            if (pc.CountryMismatch && pc.ExpectedCountry is { } exp)
            {
                left.Add(new($"  ⚠ {L("name says", "в названии")} {GeoIp.Flag(exp)}", Theme.A(Theme.Yellow, null, TextStyle.Bold)));
            }
        }

        var inbound = Config.Inbound.First();
        var sys = Config.SystemProxyItem.SysProxyType switch
        {
            ESysProxyType.ForcedClear => L("clear", "очистить"),
            ESysProxyType.ForcedChange => L("set", "установлен"),
            ESysProxyType.Pac => "PAC",
            _ => L("unchanged", "не менять"),
        };
        var tun = Config.TunModeItem.EnableTun;
        var tunNoAccess = tun && pc.CoreRunning && !pc.TunActive;
        var right = new List<SegmentBar.Segment>
        {
            new($"⇄ :{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}{(inbound.AllowLANConn ? " LAN" : "")}  ", Theme.A(Theme.Sub)),
            new($"{L("sysproxy", "сист.прокси")}: {sys}  ", Theme.A(Theme.Sub)),
            tunNoAccess || (pc.TunActive && pc.TunUp == false)
                ? new(" TUN ⚠ ", Theme.A(Theme.Crust, Theme.Peach, TextStyle.Bold))
                : new(tun ? $" TUN{(pc.SystemExitGeo is { } sysGeo ? " ⇢ " + GeoIp.Flag(sysGeo.Country) : "")} " : "TUN ",
                    tun ? Theme.A(Theme.Crust, Theme.Teal, TextStyle.Bold) : Theme.A(Theme.Dim)),
            new($"  ⤳ {_routingName} ", Theme.A(Theme.Sub)),
        };
        _header.Set(left, right);
    }

    private void UpdateStatusLine()
    {
        var segs = new List<SegmentBar.Segment> { new(" ", Theme.A(Theme.Text)) };
        var bg = BackgroundScheduler.Instance;
        var st = AppHost.Settings;
        var bgJob = TestService.Instance.RunningJobs.FirstOrDefault(j => j.Background);
        if (bg.IsRunning)
        {
            segs.Add(new($"⟳ {L("Background", "Фон")}: ", Theme.A(Theme.Blue, null, TextStyle.Bold)));
            if (bgJob != null)
            {
                segs.Add(new($"{bg.Stage} · {Phase(bgJob.Phase)} {Theme.Bar(bgJob.PhaseDone, Math.Max(1, bgJob.PhaseTotal), 12)} {bgJob.PhaseDone}/{bgJob.PhaseTotal}", Theme.A(Theme.Blue)));
                segs.Add(new($"  ✓{bgJob.Alive} ✗{bgJob.Failed}", Theme.A(Theme.Sub)));
            }
            else
            {
                segs.Add(new(bg.Stage.IsNotEmpty() ? bg.Stage + "…" : "…", Theme.A(Theme.Blue)));
            }
        }
        else if (st.BackgroundEnabled)
        {
            segs.Add(new($"● {L("Background", "Фон")}: {L("on", "вкл")}", Theme.A(Theme.Green, null, TextStyle.Bold)));
            segs.Add(new($" · {L("every", "каждые")} {st.BackgroundIntervalMinutes} {L("min", "мин")} · {L("next", "след.")} {(bg.NextRun is { } next ? next.ToString("HH:mm") : "…")}", Theme.A(Theme.Sub)));
            if (bg.LastRun is { } last)
            {
                segs.Add(new($" · {L("last", "посл.")} {last:HH:mm}: {bg.LastResult}", Theme.A(Theme.Dim)));
            }
        }
        else
        {
            segs.Add(new($"○ {L("Background", "Фон")}: {L("off", "выкл")} ", Theme.A(Theme.Dim, null, TextStyle.Bold)));
            segs.Add(new(L("(b — turn on)", "(b — включить)"), Theme.A(Theme.Dim)));
        }
        if (AppHost.Settings.AliveEnabled)
        {
            segs.Add(new($"   ★ {AppHost.Settings.AliveName} {_aliveCount}", Theme.A(Theme.Yellow, null, TextStyle.Bold)));
        }
        foreach (var j in TestService.Instance.Jobs.Where(j => !j.Background && (j.IsRunning || (DateTime.Now - j.Finished!.Value).TotalSeconds < 15)))
        {
            segs.Add(new($"   ▶ {j.Title} ", Theme.A(Theme.Mauve, null, TextStyle.Bold)));
            segs.Add(new(j.IsRunning ? $"{Theme.Bar(j.PhaseDone, Math.Max(1, j.PhaseTotal), 8)} {j.PhaseDone}/{j.PhaseTotal}" : j.ProgressText, Theme.A(Theme.Mauve)));
        }
        if ((DateTime.Now - _noticeAt).TotalSeconds < 8)
        {
            segs.Add(new($"   {_notice}", Theme.A(Theme.Yellow)));
        }
        _statusLine.Set(segs);
    }

    private int _aliveCount;

    private static string Phase(string phase) => phase switch
    {
        "ping" => L("ping", "пинг"),
        "speed" => L("speed", "скорость"),
        _ => phase,
    };

    private string _routingName = "";

    private async Task RefreshRoutingNameAsync()
    {
        var items = await ProxyController.Instance.GetRoutingsAsync();
        var active = items.FirstOrDefault(r => r.IsActive) ?? items.FirstOrDefault(r => r.Id == Config.RoutingBasicItem.RoutingIndexId);
        _routingName = active?.Remarks ?? "";
        _stateDirty = true;
    }

    #endregion status texts

    #region table style

    private void ConfigureTableStyle()
    {
        _table.SetScheme(Theme.Base);
        var st = _table.Style;
        st.ShowHorizontalHeaderOverline = false;
        st.ShowVerticalCellLines = false;
        st.ShowVerticalHeaderLines = false;
        st.ShowHorizontalHeaderUnderline = true;
        st.ExpandLastColumn = true;
        st.SmoothHorizontalScrolling = true;
        st.HeaderScheme = Theme.Flat(Theme.A(Theme.Sub, null, TextStyle.Bold));
        st.ShowVerticalCellLineForFirstColumn = false;
        st.ShowVerticalCellLineForLastColumn = false;
        st.AlwaysUseNormalColorForVerticalCellLines = true;

        st.GetOrCreateColumnStyle(ServerTableSource.ColMark).MaxWidth = 1;
        var ip = st.GetOrCreateColumnStyle(ServerTableSource.ColIp);
        ip.MinWidth = 2;
        ip.MaxWidth = 3;
        ip.ColorGetter = a => a.RowIndex >= 0 && a.RowIndex < _rows.Count && GeoIp.IpInfoMismatch(_rows[a.RowIndex].IpInfo)
            ? a.RowScheme with { Normal = Theme.A(Theme.Yellow, Theme.MismatchBg) }
            : null;
        st.GetOrCreateColumnStyle(ServerTableSource.ColName).MaxWidth = 40;
        st.GetOrCreateColumnStyle(ServerTableSource.ColType).MaxWidth = 7;
        var addr = st.GetOrCreateColumnStyle(ServerTableSource.ColAddress);
        addr.MaxWidth = 28;
        addr.ColorGetter = a => Dimmed(a);
        var transport = st.GetOrCreateColumnStyle(ServerTableSource.ColTransport);
        transport.MaxWidth = 12;
        transport.ColorGetter = a => Dimmed(a);
        var sub = st.GetOrCreateColumnStyle(ServerTableSource.ColSub);
        sub.MaxWidth = 14;
        sub.ColorGetter = a => Dimmed(a);
        var delay = st.GetOrCreateColumnStyle(ServerTableSource.ColDelay);
        delay.MinWidth = 8;
        delay.MaxWidth = 10;
        delay.Alignment = Alignment.End;
        delay.ColorGetter = a => CellScheme(a, DelayColor(a.RowIndex));
        var speed = st.GetOrCreateColumnStyle(ServerTableSource.ColSpeed);
        speed.MinWidth = 14;
        speed.MaxWidth = 16;
        speed.ColorGetter = a => CellScheme(a, SpeedColor(a.RowIndex));

        st.RowColorGetter = a =>
        {
            if (a.RowIndex < 0 || a.RowIndex >= _rows.Count)
            {
                return null;
            }
            var r = _rows[a.RowIndex];
            var baseScheme = _table.GetScheme();
            if (r.IsActive)
            {
                return baseScheme with { Normal = Theme.A(Theme.Green, null, TextStyle.Bold) };
            }
            if (r.Marked)
            {
                return baseScheme with { Normal = Theme.A(Theme.Yellow) };
            }
            if (r.Delay < 0)
            {
                return baseScheme with { Normal = Theme.A(Theme.Dim) };
            }
            return null;
        };
    }

    private static Scheme? Dimmed(CellColorGetterArgs a) =>
        a.RowScheme.Normal.Foreground == Theme.Text ? a.RowScheme with { Normal = Theme.A(Theme.Sub) } : null;

    private Color? DelayColor(int row)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return null;
        }
        var r = _rows[row];
        if (r.DelayStatus.IsNotEmpty())
        {
            return Theme.Blue;
        }
        return r.Delay == 0 ? null : Theme.DelayColor(r.Delay);
    }

    private Color? SpeedColor(int row)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return null;
        }
        var r = _rows[row];
        if (r.SpeedStatus.IsNotEmpty())
        {
            return Theme.Blue;
        }
        return r.Speed <= 0 ? null : Theme.SpeedColor(r.Speed);
    }

    private static Scheme? CellScheme(CellColorGetterArgs a, Color? fg)
    {
        if (fg is null)
        {
            return null;
        }
        var n = a.RowScheme.Normal;
        return a.RowScheme with { Normal = Theme.A(fg.Value, n.Background, n.Style) };
    }

    #endregion table style

    #region helpers

    /// <summary>Runs async work off the UI thread; errors go to the log.</summary>
    private void Fire(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                ServiceLib.Common.Logging.SaveLog("TUI", ex);
                LogBus.Notice(ex.Message);
            }
        });
    }

    /// <summary>Executes on the UI thread and returns the result to the calling (worker) thread.</summary>
    private Task<T> OnUi<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        App!.Invoke(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    #endregion helpers
}
