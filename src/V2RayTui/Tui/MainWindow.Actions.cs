using System.Text;
using QRCoder;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using V2RayTui.Cli;

namespace V2RayTui.Tui;

internal sealed partial class MainWindow
{
    private void OnGlobalKey(object? sender, Key key)
    {
        // Only while the main window is on top (not inside dialogs).
        if (key.Handled || App?.TopRunnableView != this)
        {
            return;
        }
        if (HandleKey(key))
        {
            key.Handled = true;
        }
    }

    private bool HandleKey(Key key)
    {
        var inSubs = _subsList.HasFocus;

        if (key == Key.Esc)
        {
            StopTests(includeBackground: false);
            return true;
        }
        if (key == Key.Tab || key == Key.Tab.WithShift)
        {
            CyclePane(key.IsShift ? -1 : 1);
            return true;
        }
        if (key == Key.F1)
        {
            ShowHelp();
            return true;
        }
        if (key == Key.F2)
        {
            SettingsMenu();
            return true;
        }
        if (key == Key.F3)
        {
            ChooseSystemProxy();
            return true;
        }
        if (key == Key.F4)
        {
            ChooseRouting();
            return true;
        }
        if (key == Key.F5)
        {
            Fire(ProxyController.Instance.ReloadAsync);
            return true;
        }
        if (key == Key.F6)
        {
            Fire(ProxyController.Instance.StopAsync);
            return true;
        }
        if (key == Key.F7)
        {
            ToggleTun();
            return true;
        }
        if (key == Key.F8)
        {
            UpdatesMenu();
            return true;
        }
        if (key == Key.F9)
        {
            BackgroundScheduler.Instance.RunNow();
            LogBus.Notice(L("Background test started", "Фоновый тест запущен"));
            return true;
        }
        if (key == Key.F10 || key == Key.Q.WithCtrl)
        {
            QuitDialog();
            return true;
        }
        if (key == Key.F.WithCtrl)
        {
            AskFilter();
            return true;
        }
        if (key == Key.A.WithCtrl)
        {
            ToggleMarkAll();
            return true;
        }
        if (key == Key.V.WithCtrl)
        {
            ImportFromClipboard();
            return true;
        }
        if (key == Key.Delete)
        {
            if (inSubs)
            {
                DeleteSub();
            }
            else
            {
                DeleteServers();
            }
            return true;
        }
        if (key == Key.Space && _table.HasFocus)
        {
            ToggleMark();
            return true;
        }
        if (key == Key.Enter && inSubs)
        {
            _table.SetFocus();
            return true;
        }

        if (KeyMap.Char(key) is not { } ch)
        {
            return false;
        }

        if (inSubs)
        {
            switch (ch)
            {
                case 'e':
                    EditSub(CurrentSub);
                    return true;
                case 'd':
                    DeleteSub();
                    return true;
            }
        }

        switch (ch)
        {
            case 'q':
                QuitDialog();
                return true;
            case '?' or 'h':
                ShowHelp();
                return true;
            case 'r':
                StartTest(TestMode.RealPing);
                return true;
            case 't':
                StartTest(TestMode.Tcping);
                return true;
            case 's':
                StartTest(TestMode.Speed);
                return true;
            case 'm':
                StartTest(TestMode.PingThenSpeed);
                return true;
            case 'x':
                StopTests(includeBackground: true);
                return true;
            case 'o':
                Fire(() => SortByResultAsync(false));
                return true;
            case 'O':
                Fire(() => SortByResultAsync(true));
                return true;
            case 'u':
                UpdateSubs(Config.SubIndexId);
                return true;
            case 'U':
                UpdateSubs("");
                return true;
            case 'a':
                EditSub(null);
                return true;
            case '/':
                AskFilter();
                return true;
            case 'p':
                ImportDialog();
                return true;
            case 'v':
                ImportFromClipboard();
                return true;
            case 'Q':
                ImportFromQrImage();
                return true;
            case 'c':
                CopyLinks();
                return true;
            case 'i':
                ShowInfo();
                return true;
            case 'e':
                ExportClientConfig();
                return true;
            case 'D':
                RemoveDuplicates();
                return true;
            case 'X':
                RemoveInvalid();
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
            case '*':
                ToggleMarkAll();
                return true;
            case 'l':
                CycleLog();
                return true;
            case 'b':
                ToggleBackground();
                return true;
            case 'g':
                GoToActive();
                return true;
            case 'w':
                CopyProxyEnv();
                return true;
            case 'n':
                // Re-check the current connection: delay, exit IP, country vs server name.
                Fire(ProxyController.Instance.CheckAvailabilityAsync);
                return true;
        }
        return false;
    }

    #region servers

    private void ConnectSelected()
    {
        if (Current is not { } row)
        {
            return;
        }
        Fire(async () =>
        {
            await ProxyController.Instance.ActivateAsync(row.IndexId);
            await OnUi(() =>
            {
                foreach (var r in _rows)
                {
                    r.IsActive = r.IndexId == Config.IndexId;
                }
                _table.SetNeedsDraw();
                return true;
            });
        });
    }

    private void GoToActive()
    {
        var idx = _rows.FindIndex(r => r.IsActive);
        if (idx >= 0)
        {
            _table.SetSelection(0, idx, false);
            _table.EnsureCursorIsVisible();
            _table.SetFocus();
        }
    }

    private void ToggleMark()
    {
        if (Current is not { } row)
        {
            return;
        }
        row.Marked = !row.Marked;
        if (row.Marked)
        {
            _marked.Add(row.IndexId);
        }
        else
        {
            _marked.Remove(row.IndexId);
        }
        var y = _table.Value?.SelectedCell.Y ?? 0;
        if (y + 1 < _rows.Count)
        {
            _table.SetSelection(0, y + 1, false);
            _table.EnsureCursorIsVisible();
        }
        UpdateServersTitle();
        _table.SetNeedsDraw();
    }

    private void ToggleMarkAll()
    {
        var mark = _rows.Any(r => !r.Marked);
        foreach (var r in _rows)
        {
            r.Marked = mark;
        }
        _marked.Clear();
        if (mark)
        {
            _marked.UnionWith(_rows.Select(r => r.IndexId));
        }
        UpdateServersTitle();
        _table.SetNeedsDraw();
    }

    private void StartTest(TestMode mode)
    {
        var scope = TestScope();
        if (scope.Count == 0)
        {
            return;
        }
        if (mode != TestMode.Tcping && !CoreUpdater.MainCores.Any(CoreUpdater.IsInstalled))
        {
            Dialogs.Error(App!, L("No core", "Нет ядра"), L("Install cores first: F8.", "Сначала установите ядра: F8."));
            return;
        }
        var title = mode switch
        {
            TestMode.Tcping => "tcping",
            TestMode.RealPing => "ping",
            TestMode.Speed => L("speed", "скорость"),
            _ => L("ping+speed", "пинг+скорость"),
        } + (_marked.Count > 0 ? $" ({scope.Count})" : CurrentSub is { } s ? $" [{s.Remarks}]" : "");
        Fire(async () =>
        {
            var profiles = await ServerRepository.ToProfilesAsync(scope);
            TestService.Instance.Start(title, mode, profiles, background: false);
            _stateDirty = true;
        });
    }

    private void StopTests(bool includeBackground)
    {
        if (TestService.Instance.RunningJobs.Count == 0)
        {
            return;
        }
        TestService.Instance.StopAll(includeBackground);
        LogBus.Notice(includeBackground ? L("All tests stopped", "Все тесты остановлены") : L("Tests stopped", "Тесты остановлены"));
    }

    private async Task SortByResultAsync(bool bySpeed)
    {
        // Sort the whole group (not only filtered rows) so the stored order stays consistent.
        var all = await ServerRepository.LoadAsync(Config.SubIndexId, "");
        foreach (var r in all)
        {
            if (_rowById.TryGetValue(r.IndexId, out var live))
            {
                r.Delay = live.Delay;
                r.Speed = live.Speed;
            }
        }
        var ordered = ServerRepository.OrderByResult(all, bySpeed).ToList();
        await ServerRepository.SaveOrderAsync(ordered);
        await ReloadServersAsync();
        LogBus.Notice(bySpeed ? L("Sorted by speed", "Отсортировано по скорости") : L("Sorted by delay", "Отсортировано по задержке"));
    }

    private void DeleteServers()
    {
        var sel = Selection();
        if (sel.Count == 0)
        {
            return;
        }
        var what = sel.Count == 1 ? sel[0].Remarks : $"{sel.Count} " + L("servers", "серверов");
        if (!Dialogs.Confirm(App!, L("Delete", "Удаление"), L($"Delete {what}?", $"Удалить {what}?")))
        {
            return;
        }
        _marked.ExceptWith(sel.Select(r => r.IndexId));
        Fire(async () =>
        {
            var profiles = await ServerRepository.ToProfilesAsync(sel);
            var activeRemoved = profiles.Any(p => p.IndexId == Config.IndexId);
            await ConfigHandler.RemoveServers(Config, profiles);
            await LoadSubsAsync();
            await ReloadServersAsync();
            if (activeRemoved && ProxyController.Instance.CoreRunning)
            {
                await ProxyController.Instance.ReloadAsync();
            }
        });
    }

    private void RemoveDuplicates()
    {
        if (!Dialogs.Confirm(App!, L("Duplicates", "Дубликаты"), L("Remove duplicate servers in this list?", "Удалить дубликаты серверов в этом списке?")))
        {
            return;
        }
        Fire(async () =>
        {
            // On this list only (so "All servers" never touches the Alive group); same server = same share link.
            var rows = await ServerRepository.LoadAsync(Config.SubIndexId, "");
            var profiles = await ServerRepository.ToProfilesAsync(rows);
            var remove = profiles
                .Where(p => !p.ConfigType.IsComplexType())
                .GroupBy(AliveGroup.Key)
                .SelectMany(g => g.Skip(1))
                .Where(p => p.IndexId != Config.IndexId)
                .ToList();
            if (remove.Count > 0)
            {
                await ConfigHandler.RemoveServers(Config, remove);
            }
            LogBus.Notice(string.Format(ResUI.RemoveDuplicateServerResult, profiles.Count, profiles.Count - remove.Count));
            await LoadSubsAsync();
            await ReloadServersAsync();
        });
    }

    private void RemoveInvalid()
    {
        if (!Dialogs.Confirm(App!, L("Failed servers", "Нерабочие серверы"),
                L("Delete the servers of this list whose last test failed?", "Удалить серверы этого списка, не прошедшие последний тест?")))
        {
            return;
        }
        Fire(async () =>
        {
            var rows = await ServerRepository.LoadAsync(Config.SubIndexId, "");
            var failed = rows.Where(r => r.Delay < 0 && !r.ConfigType.IsComplexType() && r.IndexId != Config.IndexId).ToList();
            if (failed.Count > 0)
            {
                await ConfigHandler.RemoveServers(Config, await ServerRepository.ToProfilesAsync(failed));
            }
            LogBus.Notice(string.Format(ResUI.RemoveInvalidServerResultTip, failed.Count));
            await LoadSubsAsync();
            await ReloadServersAsync();
        });
    }

    private void Move(EMove dir)
    {
        if (Current is not { } row || _filter.IsNotEmpty())
        {
            return;
        }
        var ids = _rows.Select(r => r.IndexId).ToList();
        var index = ids.IndexOf(row.IndexId);
        Fire(async () =>
        {
            if (await ConfigHandler.MoveServer(Config, ids, index, dir) == 0)
            {
                await ReloadServersAsync(row.IndexId);
            }
        });
    }

    private void CopyLinks()
    {
        var sel = Selection();
        if (sel.Count == 0)
        {
            return;
        }
        Fire(async () =>
        {
            var profiles = await ServerRepository.ToProfilesAsync(sel);
            var text = string.Join("\n", profiles.Select(FmtHandler.GetShareUri).Where(u => u.IsNotEmpty()));
            await OnUi(() =>
            {
                Clip.Set(App!, text);
                return true;
            });
        });
    }

    private void CopyProxyEnv()
    {
        var address = $"{Global.Loopback}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}";
        var text = $"export http_proxy=http://{address} https_proxy=http://{address} all_proxy=socks5://{address}\n" +
                   $"export HTTP_PROXY=http://{address} HTTPS_PROXY=http://{address} ALL_PROXY=socks5://{address}";
        Clip.Set(App!, text);
    }

    private void ShowInfo()
    {
        if (Current is not { } row)
        {
            return;
        }
        Fire(async () =>
        {
            var p = await AppManager.Instance.GetProfileItem(row.IndexId);
            if (p is null)
            {
                return;
            }
            var url = FmtHandler.GetShareUri(p) ?? "";
            var sb = new StringBuilder();
            sb.AppendLine(p.GetSummary());
            sb.AppendLine($"{L("Type", "Тип")}: {p.ConfigType}   {L("Core", "Ядро")}: {AppManager.Instance.GetCoreType(p, p.ConfigType)}");
            sb.AppendLine($"{L("Address", "Адрес")}: {p.Address}:{p.Port}");
            sb.AppendLine($"{L("Transport", "Транспорт")}: {p.GetNetwork()} {p.StreamSecurity} sni={p.Sni} fp={p.Fingerprint}");
            sb.AppendLine($"{L("Subscription", "Подписка")}: {row.SubRemarks}");
            sb.AppendLine($"{L("Delay", "Задержка")}: {row.Delay} ms   {L("Speed", "Скорость")}: {row.Speed} MB/s   IP: {row.IpInfo}");
            sb.AppendLine();
            sb.AppendLine(url);
            if (url.IsNotEmpty())
            {
                try
                {
                    using var gen = new QRCodeGenerator();
                    using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
                    sb.AppendLine();
                    sb.Append(new AsciiQRCode(data).GetGraphicSmall(drawQuietZones: true, invert: true, endOfLine: "\n"));
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"QR: {ex.Message}");
                }
            }
            await OnUi(() =>
            {
                Dialogs.ShowText(App!, row.Remarks, sb.ToString(), url);
                return true;
            });
        });
    }

    private void ExportClientConfig()
    {
        if (Current is not { } row)
        {
            return;
        }
        var safe = string.Concat(row.Remarks.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var path = Dialogs.Prompt(App!, L("Export client config", "Экспорт клиентского конфига"),
            L("File:", "Файл:"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"v2ray-{safe}.json"));
        if (path.IsNullOrEmpty())
        {
            return;
        }
        Fire(async () =>
        {
            var p = await AppManager.Instance.GetProfileItem(row.IndexId);
            if (p is null)
            {
                return;
            }
            var (context, validator) = await CoreConfigContextBuilder.Build(Config, p);
            if (NoticeManager.Instance.NotifyValidatorResult(validator) && !validator.Success)
            {
                return;
            }
            var result = await CoreConfigHandler.GenerateClientConfig(context, path);
            LogBus.Notice(result.Success ? string.Format(ResUI.SaveClientConfigurationIn, path) : result.Msg);
        });
    }

    private void AskFilter()
    {
        var f = Dialogs.Prompt(App!, L("Filter", "Фильтр"), L("Name or address contains (empty = off):", "Имя или адрес содержит (пусто — выключить):"), _filter);
        if (f is null)
        {
            return;
        }
        _filter = f.Trim();
        Fire(() => ReloadServersAsync());
    }

    #endregion servers

    #region import

    private void ImportDialog()
    {
        var text = Dialogs.MultiLine(App!, L("Import", "Импорт"),
            L("Share links (vmess://, vless://, ss://, trojan://, hysteria2://, …), base64, or a subscription URL:",
              "Ссылки (vmess://, vless://, ss://, trojan://, hysteria2://, …), base64 или URL подписки:"));
        if (text.IsNotEmpty())
        {
            Import(text);
        }
    }

    private void ImportFromClipboard()
    {
        var text = Clip.Get(App!);
        if (text.IsNullOrEmpty())
        {
            // No clipboard tool: fall back to the paste dialog (terminal paste works there).
            ImportDialog();
            return;
        }
        Import(text);
    }

    private void ImportFromQrImage()
    {
        var file = Dialogs.Prompt(App!, L("Import QR code", "Импорт QR-кода"), L("Image file:", "Файл изображения:"));
        if (file.IsNullOrEmpty())
        {
            return;
        }
        Fire(async () =>
        {
            var text = QRCodeUtils.ParseBarcode(file.Trim());
            if (text.IsNullOrEmpty())
            {
                LogBus.Notice(ResUI.NoValidQRcodeFound);
                return;
            }
            await OnUi(() =>
            {
                Import(text);
                return true;
            });
        });
    }

    private void OnPaste(object? sender, PasteEventArgs e)
    {
        if (App?.TopRunnableView != this || e.Handled || !e.Text.Contains("://"))
        {
            return;
        }
        e.Handled = true;
        var lines = e.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        if (Dialogs.Confirm(App!, L("Import", "Импорт"), L($"Import pasted text ({lines} lines)?", $"Импортировать вставленный текст ({lines} строк)?")))
        {
            Import(e.Text);
        }
    }

    private void Import(string text)
    {
        Fire(async () =>
        {
            var count = await CliApp.ImportText(text, Config.SubIndexId);
            if (count > 0)
            {
                LogBus.Notice(string.Format(ResUI.SuccessfullyImportedServerViaClipboard, count));
                await LoadSubsAsync();
                await ReloadServersAsync();
            }
            else
            {
                LogBus.Notice(ResUI.OperationFailed);
            }
        });
    }

    #endregion import

    #region subscriptions

    private void UpdateSubs(string? subId)
    {
        var viaProxy = ProxyController.Instance.CoreRunning;
        LogBus.Notice(L("Updating subscriptions", "Обновление подписок") + (viaProxy ? L(" (via proxy)", " (через прокси)") : ""));
        Fire(async () =>
        {
            var r = await ProxyController.Instance.UpdateSubscriptionsAsync(subId, viaProxy);
            if (r.Updated == 0 && r.Failed == 0 && r.Skipped.Count > 0)
            {
                var min = AppHost.Settings.SubUpdateMinIntervalMinutes;
                var next = r.Skipped.Min(x => x.NextAllowed);
                var force = await OnUi(() => Dialogs.Confirm(App!, L("Update subscriptions", "Обновление подписок"),
                    L($"Updated less than {min} min ago — next update not before {next:HH:mm}.\nUpdate anyway? The provider may limit frequent requests.",
                      $"Обновлялись менее {min} мин назад — следующее обновление не раньше {next:HH:mm}.\nОбновить принудительно? Провайдер может ограничивать частые запросы.")));
                if (!force)
                {
                    return;
                }
                r = await ProxyController.Instance.UpdateSubscriptionsAsync(subId, viaProxy, force: true);
            }
            if (r.Any && AppHost.Settings.TestAfterSubUpdate)
            {
                await OnUi(() =>
                {
                    StartTest(TestMode.RealPing);
                    return true;
                });
            }
        });
    }

    private void EditSub(SubItem? existing)
    {
        var item = existing ?? new SubItem { Id = string.Empty, Enabled = true };
        if (!SubEditDialog.Run(App!, item))
        {
            return;
        }
        Fire(async () =>
        {
            if (await ConfigHandler.AddSubItem(Config, item) != 0)
            {
                LogBus.Notice(ResUI.OperationFailed);
                return;
            }
            await LoadSubsAsync();
            if (existing is null && item.Url.IsNotEmpty())
            {
                // Fetch the new subscription right away.
                var added = (await AppManager.Instance.SubItems() ?? []).LastOrDefault(s => s.Url == item.Url);
                if (added != null)
                {
                    await OnUi(() =>
                    {
                        UpdateSubs(added.Id);
                        return true;
                    });
                }
            }
        });
    }

    private void DeleteSub()
    {
        if (CurrentSub is not { } sub)
        {
            return;
        }
        if (!Dialogs.Confirm(App!, L("Delete subscription", "Удаление подписки"),
                L($"Delete \"{sub.Remarks}\" and its servers?", $"Удалить «{sub.Remarks}» и её серверы?")))
        {
            return;
        }
        Fire(async () =>
        {
            await ConfigHandler.DeleteSubItem(Config, sub.Id);
            Config.SubIndexId = "";
            await LoadSubsAsync();
            await ReloadServersAsync();
        });
    }

    #endregion subscriptions

    #region proxy / routing / tun / updates

    private void ChooseSystemProxy()
    {
        var types = new List<ESysProxyType> { ESysProxyType.ForcedClear, ESysProxyType.ForcedChange, ESysProxyType.Unchanged };
        if (Utils.IsWindows())
        {
            types.Add(ESysProxyType.Pac);
        }
        var names = types.Select(t => t switch
        {
            ESysProxyType.ForcedClear => ResUI.menuSystemProxyClear,
            ESysProxyType.ForcedChange => ResUI.menuSystemProxySet,
            ESysProxyType.Unchanged => ResUI.menuSystemProxyNothing,
            _ => ResUI.menuSystemProxyPac,
        }).ToList();
        var sel = Dialogs.Choose(App!, L("System proxy", "Системный прокси"), names, types.IndexOf(Config.SystemProxyItem.SysProxyType));
        if (sel is { } i)
        {
            Fire(() => ProxyController.Instance.SetSystemProxyAsync(types[i]));
        }
    }

    private void ChooseRouting()
    {
        using var w = new RoutingSetsWindow();
        App!.Run(w);
        Fire(async () =>
        {
            if (w.Modified && ProxyController.Instance.CoreRunning)
            {
                await ProxyController.Instance.ReloadAsync();
            }
            await RefreshRoutingNameAsync();
        });
    }

    private void ToggleTun()
    {
        var enable = !Config.TunModeItem.EnableTun;
        string? pwd = null;
        if (enable && !Utils.IsWindows() && !ProxyController.TunAllowed && !Task.Run(ProxyController.TryPasswordlessSudoAsync).GetAwaiter().GetResult())
        {
            pwd = Dialogs.Prompt(App!, "TUN", L("sudo password (kept in memory only):", "Пароль sudo (хранится только в памяти):"), secret: true);
            if (pwd.IsNullOrEmpty())
            {
                return;
            }
        }
        Fire(() => ProxyController.Instance.SetTunAsync(enable, pwd));
    }

    private void UpdatesMenu()
    {
        string Status(ECoreType t) => CoreUpdater.IsInstalled(t) ? "" : L(" (missing)", " (нет)");
        var items = new List<string>
        {
            L("Update Xray", "Обновить Xray") + Status(ECoreType.Xray),
            L("Update sing-box", "Обновить sing-box") + Status(ECoreType.sing_box),
            L("Update geo files", "Обновить geo-файлы") + (CoreUpdater.GeoFilesPresent ? "" : L(" (missing)", " (нет)")),
            L("Update everything", "Обновить всё"),
        };
        var sel = Dialogs.Choose(App!, L("Updates (from GitHub)", "Обновления (с GitHub)"), items, 3);
        if (sel is not { } i)
        {
            return;
        }
        var viaProxy = ProxyController.Instance.CoreRunning;
        Fire(async () =>
        {
            if (i is 0 or 3)
            {
                await CoreUpdater.UpdateCoreAsync(ECoreType.Xray, viaProxy);
            }
            if (i is 1 or 3)
            {
                await CoreUpdater.UpdateCoreAsync(ECoreType.sing_box, viaProxy);
            }
            if (i is 2 or 3)
            {
                await CoreUpdater.UpdateGeoAsync(viaProxy);
                LogBus.Notice(L("Geo files updated", "Geo-файлы обновлены"));
            }
        });
    }

    private void ToggleBackground()
    {
        AppHost.Settings.BackgroundEnabled = !AppHost.Settings.BackgroundEnabled;
        AppHost.SaveSettings();
        BackgroundScheduler.Instance.Reschedule();
        LogBus.Notice(AppHost.Settings.BackgroundEnabled
            ? L($"Background tests on (every {AppHost.Settings.BackgroundIntervalMinutes} min)", $"Фоновые тесты включены (каждые {AppHost.Settings.BackgroundIntervalMinutes} мин)")
            : L("Background tests off", "Фоновые тесты выключены"));
    }

    #endregion proxy / routing / tun / updates

    #region misc

    /// <summary>Explicit pane cycling (FrameViews are separate tab groups in Terminal.Gui v2).</summary>
    private void CyclePane(int dir)
    {
        View[] panes = _logFrame.Visible ? [_subsList, _table, _logList] : [_subsList, _table];
        var i = Array.FindIndex(panes, p => p.HasFocus);
        panes[((i < 0 ? 1 : i) + dir + panes.Length) % panes.Length].SetFocus();
    }

    private void CycleLog()
    {
        _logHeight = _logHeight switch
        {
            8 => 18,
            18 => 0,
            _ => 8,
        };
        _logFrame.Visible = _logHeight > 0;
        SetNeedsLayout();
    }

    private void SettingsMenu()
    {
        var sel = Dialogs.Choose(App!, L("Settings", "Настройки"),
        [
            L("Tests & background", "Тесты и фоновый режим"),
            L("Local proxy (ports, LAN, sniffing, logs)", "Локальный прокси (порты, LAN, sniffing, логи)"),
            L("Test URLs & timeouts", "URL и таймауты тестов"),
            L("Regional preset (routing / geo / DNS)", "Региональный пресет (маршруты / geo / DNS)"),
            L("Alive group (live fast servers)", "Группа Alive (живые быстрые серверы)"),
        ]);
        if (sel == 4)
        {
            var wasOn = AppHost.Settings.AliveEnabled;
            if (SettingsDialogs.Alive(App!))
            {
                TestService.Instance.ApplySettings(AppHost.Settings);
                BackgroundScheduler.Instance.Reschedule();
                _stateDirty = true;
                if (AppHost.Settings.AliveEnabled && (!wasOn || Dialogs.Confirm(App!, AppHost.Settings.AliveName,
                        L("Run the cycle now?", "Запустить цикл сейчас?"))))
                {
                    BackgroundScheduler.Instance.RunNow();
                }
            }
            return;
        }
        if (sel == 3)
        {
            var presets = Enum.GetValues<EPresetType>();
            if (Dialogs.Choose(App!, L("Regional preset", "Региональный пресет"), presets.Select(p => p.ToString()).ToList()) is { } pi)
            {
                Fire(async () =>
                {
                    await ProxyController.Instance.ApplyRegionalPresetAsync(presets[pi]);
                    await RefreshRoutingNameAsync();
                });
            }
            return;
        }
        var changed = sel switch
        {
            0 => SettingsDialogs.Tests(App!, _subs),
            1 => SettingsDialogs.Proxy(App!),
            2 => SettingsDialogs.TestUrls(App!),
            _ => false,
        };
        if (!changed)
        {
            return;
        }
        TestService.Instance.ApplySettings(AppHost.Settings);
        BackgroundScheduler.Instance.Reschedule();
        _stateDirty = true;
        if (sel == 1 && ProxyController.Instance.CoreRunning)
        {
            Fire(ProxyController.Instance.ReloadAsync);
        }
    }

    private void QuitDialog()
    {
        var bgHint = AppHost.Settings.BackgroundEnabled
            ? L("the proxy keeps working and servers are tested on schedule.", "прокси продолжит работать, серверы будут тестироваться по расписанию.")
            : L("the proxy keeps working.", "прокси продолжит работать.");
        var r = MessageBox.Query(App!, L("Quit", "Выход"),
            L($"Keep running in background? — {bgHint}", $"Оставить работать в фоне? — {bgHint}"),
            L("_Cancel", "_Отмена"), L("_Background", "В _фон"), L("_Quit", "_Выйти"));
        switch (r)
        {
            case 1:
                ExitMode = TuiExit.Detach;
                App!.RequestStop();
                break;
            case 2:
                ExitMode = TuiExit.Quit;
                App!.RequestStop();
                break;
        }
    }

    private void ShowHelp()
    {
        Dialogs.ShowText(App!, L("Keys", "Клавиши"), L(HelpEn, HelpRu));
    }

    private const string HelpEn =
"""
Servers                                   Tests (parallel, run in background)
  Enter      connect (make active)          r   real ping (marked or all shown)
  Space      mark / unmark                  t   tcping
  * Ctrl+A   mark all / none                s   ping + download speed
  g          jump to active server          m   ping all, then speed of top-N
  i          details, share link, QR        Esc stop my tests    x  stop all incl. background
  c          copy share link(s)             o / O  sort by delay / by speed (saved)
  e          export client config (json)    F9  run background cycle now
  Del        delete                         b   background tests on / off
  D / X      remove duplicates / failed
  [ ] { }    move up / down / top / bottom
  / Ctrl+F   filter by name or address

Import                                    Subscriptions (left pane, Tab to switch)
  p          paste links / base64 / URL      a   add       e  edit      Del  delete
  v Ctrl+V   import from clipboard           u   update current   U  update all
  Q          import QR code from image       (updates go through the proxy when it is running)
  (terminal paste into the window works too)

Proxy                                     Other
  F5 restart core     F6 stop core          F2  settings             l  log size
  F3 system proxy     F4 routing rules      F8  update cores / geo   w  copy proxy env vars
  F7 TUN (sudo)                             n   check connection: delay, exit IP, country vs name
                                            q F10  quit / keep running in background

Hotkeys also work with the Russian keyboard layout.
""";

    private const string HelpRu =
"""
Серверы                                   Тесты (параллельно, в фоне)
  Enter      подключить (сделать активным)  r   реальный пинг (отмеченных или всех видимых)
  Space      отметить / снять               t   tcping
  * Ctrl+A   отметить все / снять           s   пинг + скорость загрузки
  g          к активному серверу            m   пинг всех, затем скорость топ-N
  i          детали, ссылка, QR-код         Esc остановить мои тесты   x  остановить все, включая фон
  c          скопировать ссылку(и)          o / O  сортировка по задержке / скорости (сохраняется)
  e          экспорт клиентского конфига    F9  запустить фоновый цикл сейчас
  Del        удалить                        b   фоновые тесты вкл / выкл
  D / X      удалить дубликаты / нерабочие
  [ ] { }    вверх / вниз / в начало / в конец
  / Ctrl+F   фильтр по имени или адресу

Импорт                                    Подписки (левая панель, Tab — переключение)
  p          вставить ссылки / base64 / URL   a   добавить   e  изменить   Del  удалить
  v Ctrl+V   импорт из буфера обмена          u   обновить текущую   U  обновить все
  Q          импорт QR-кода из картинки       (обновление идёт через прокси, если он запущен)
  (можно просто вставить текст в окно терминала)

Прокси                                    Прочее
  F5 перезапуск ядра  F6 остановить ядро    F2  настройки            l  размер журнала
  F3 системный прокси F4 правила маршрутов  F8  обновить ядра / geo  w  скопировать переменные прокси
  F7 TUN (sudo)                             n   проверить подключение: задержка, IP, страна vs название
                                            q F10  выход / оставить работать в фоне

Горячие клавиши работают и в русской раскладке.
""";

    #endregion misc
}
