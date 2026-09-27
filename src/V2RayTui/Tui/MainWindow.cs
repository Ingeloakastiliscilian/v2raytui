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

    private readonly Label _header;
    private readonly FrameView _subsFrame;
    private readonly ListView _subsList;
    private readonly FrameView _serversFrame;
    private readonly TableView _table;
    private readonly FrameView _logFrame;
    private readonly ListView _logList;
    private readonly Label _statusLine;

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

        _header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };

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

        _statusLine = new Label { X = 0, Y = Pos.Bottom(_logFrame), Width = Dim.Fill(), Height = 1 };

        var statusBar = new StatusBar();
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
            if (_started && e.NewValue is int i)
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

    private void OnJobChanged(TestJob job)
    {
        _stateDirty = true;
        if (!job.IsRunning && !job.Background && AppHost.Settings.SortAfterTest && !job.Cancelled)
        {
            Fire(() => SortByResultAsync(job.Mode is TestMode.Speed or TestMode.PingThenSpeed));
        }
    }

    private void OnServersChanged() => Fire(async () =>
    {
        await LoadSubsAsync();
        await ReloadServersAsync();
    });

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

    private async Task LoadSubsAsync()
    {
        var subs = await AppManager.Instance.SubItems() ?? [];
        var counts = (await AppManager.Instance.ProfileItems("") ?? [])
            .GroupBy(p => p.Subid ?? "")
            .ToDictionary(g => g.Key, g => g.Count());
        await OnUi(() =>
        {
            _subs = subs;
            _subsItems.Clear();
            _subsItems.Add($"{L("All servers", "Все серверы"),-18}{counts.Values.Sum(),5}");
            foreach (var s in subs)
            {
                var name = (s.Id == AliveGroup.CurrentId ? "★ " : s.Enabled ? "" : "·") + s.Remarks;
                if (name.Length > 17)
                {
                    name = name[..16] + "…";
                }
                _subsItems.Add($"{name,-18}{counts.GetValueOrDefault(s.Id ?? "", 0),5}");
            }
            var idx = Config.SubIndexId.IsNullOrEmpty() ? 0 : subs.FindIndex(s => s.Id == Config.SubIndexId) + 1;
            if (idx <= 0)
            {
                idx = 0;
                Config.SubIndexId = "";
            }
            _subsList.SelectedItem = idx;
            return true;
        });
    }

    private void OnSubSelected(int index)
    {
        var id = index <= 0 || index > _subs.Count ? "" : _subs[index - 1].Id;
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

        var pc = ProxyController.Instance;
        var sys = Config.SystemProxyItem.SysProxyType switch
        {
            ESysProxyType.ForcedClear => L("clear", "очистить"),
            ESysProxyType.ForcedChange => L("set", "установить"),
            ESysProxyType.Pac => "PAC",
            _ => L("unchanged", "не менять"),
        };
        var routing = Config.RoutingBasicItem.RoutingIndexId;
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var run = pc.CoreRunning ? "●" : "○";
        _header.Text = $" {run} {pc.RunningSummary}  {pc.AvailabilityText}  │ mixed:{port}{(Config.Inbound.First().AllowLANConn ? "+LAN" : "")} │ {L("sysproxy", "сист.прокси")}: {sys} │ TUN: {(Config.TunModeItem.EnableTun ? L("on", "вкл") : L("off", "выкл"))} │ {_routingName}";

        var jobs = TestService.Instance.Jobs.Where(j => j.IsRunning || (DateTime.Now - j.Finished!.Value).TotalSeconds < 15).ToList();
        var jobText = string.Join("  ", jobs.Select(j => $"[{j.Title}] {j.ProgressText}"));
        var bg = BackgroundScheduler.Instance;
        var bgText = AppHost.Settings.BackgroundEnabled
            ? bg.IsRunning
                ? L("BG: running", "Фон: идёт")
                : $"{L("BG: next", "Фон: след.")} {bg.NextRun:HH:mm}"
            : L("BG: off", "Фон: выкл");
        if (AppHost.Settings.AliveEnabled && bg.LastRun is { } last)
        {
            bgText += $" ({L("last", "посл.")} {last:HH:mm}: {bg.LastResult})";
        }
        var notice = (DateTime.Now - _noticeAt).TotalSeconds < 8 ? " │ " + _notice : "";
        _statusLine.Text = $" {bgText}{(jobText.Length > 0 ? " │ " + jobText : "")}{notice}";
    }

    private string _routingName = "";

    private async Task RefreshRoutingNameAsync()
    {
        var items = await ProxyController.Instance.GetRoutingsAsync();
        var active = items.FirstOrDefault(r => r.IsActive) ?? items.FirstOrDefault(r => r.Id == Config.RoutingBasicItem.RoutingIndexId);
        _routingName = active != null ? $"{L("routing", "маршруты")}: {active.Remarks}" : "";
        _stateDirty = true;
    }

    #endregion status texts

    #region table style

    private void ConfigureTableStyle()
    {
        var st = _table.Style;
        st.ShowHorizontalHeaderOverline = false;
        st.ShowVerticalCellLines = false;
        st.ShowVerticalHeaderLines = false;
        st.ShowHorizontalHeaderUnderline = true;
        st.ExpandLastColumn = true;
        st.SmoothHorizontalScrolling = true;

        st.GetOrCreateColumnStyle(ServerTableSource.ColMark).MaxWidth = 2;
        st.GetOrCreateColumnStyle(ServerTableSource.ColType).MaxWidth = 11;
        st.GetOrCreateColumnStyle(ServerTableSource.ColName).MaxWidth = 38;
        st.GetOrCreateColumnStyle(ServerTableSource.ColAddress).MaxWidth = 30;
        st.GetOrCreateColumnStyle(ServerTableSource.ColTransport).MaxWidth = 12;
        st.GetOrCreateColumnStyle(ServerTableSource.ColSub).MaxWidth = 14;
        var delay = st.GetOrCreateColumnStyle(ServerTableSource.ColDelay);
        delay.MinWidth = 8;
        delay.MaxWidth = 10;
        delay.Alignment = Alignment.End;
        delay.ColorGetter = a => CellScheme(a, DelayColor(a.RowIndex));
        var speed = st.GetOrCreateColumnStyle(ServerTableSource.ColSpeed);
        speed.MinWidth = 9;
        speed.MaxWidth = 14;
        speed.Alignment = Alignment.End;
        speed.ColorGetter = a => CellScheme(a, SpeedColor(a.RowIndex));

        st.RowColorGetter = a =>
        {
            if (a.RowIndex < 0 || a.RowIndex >= _rows.Count)
            {
                return null;
            }
            var r = _rows[a.RowIndex];
            var baseScheme = _table.GetScheme();
            var n = baseScheme.Normal;
            if (r.IsActive)
            {
                return baseScheme with { Normal = new Attribute(new Color(ColorName16.BrightGreen), n.Background, TextStyle.Bold) };
            }
            if (r.Marked)
            {
                return baseScheme with { Normal = new Attribute(new Color(ColorName16.BrightYellow), n.Background) };
            }
            return null;
        };
    }

    private Color? DelayColor(int row)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return null;
        }
        var r = _rows[row];
        if (r.DelayStatus.IsNotEmpty())
        {
            return new Color(ColorName16.Cyan);
        }
        return r.Delay switch
        {
            < 0 => new Color(ColorName16.BrightRed),
            0 => null,
            < 300 => new Color(ColorName16.BrightGreen),
            < 800 => new Color(ColorName16.Yellow),
            _ => new Color(ColorName16.Red),
        };
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
            return new Color(ColorName16.Cyan);
        }
        return r.Speed switch
        {
            <= 0 => null,
            < 1 => new Color(ColorName16.Yellow),
            _ => new Color(ColorName16.BrightGreen),
        };
    }

    private static Scheme? CellScheme(CellColorGetterArgs a, Color? fg)
    {
        if (fg is null)
        {
            return null;
        }
        var n = a.RowScheme.Normal;
        return a.RowScheme with { Normal = new Attribute(fg.Value, n.Background, n.Style) };
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
