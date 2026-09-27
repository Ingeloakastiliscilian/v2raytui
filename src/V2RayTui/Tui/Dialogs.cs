using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace V2RayTui.Tui;

/// <summary>Small modal helpers. All must be called on the UI thread.</summary>
internal static class Dialogs
{
    private static string L(string en, string ru) => Loc.T(en, ru);

    public static bool Confirm(IApplication app, string title, string message) =>
        MessageBox.Query(app, title, message, L("_No", "_Нет"), L("_Yes", "_Да")) == 1;

    public static void Info(IApplication app, string title, string message) =>
        MessageBox.Query(app, title, message, "_OK");

    public static void Error(IApplication app, string title, string message) =>
        MessageBox.ErrorQuery(app, title, message, "_OK");

    public static string? Prompt(IApplication app, string title, string label, string initial = "", bool secret = false)
    {
        using var dlg = new Dialog { Title = title, Width = Dim.Percent(70), Height = 8 };
        var tf = new TextField { X = 1, Y = 2, Width = Dim.Fill(1), Text = initial, Secret = secret };
        dlg.Add(new Label { Text = label, X = 1, Y = 1 }, tf);
        dlg.AddButton(new Button { Title = L("_Cancel", "_Отмена") });
        dlg.AddButton(new Button { Title = "_OK" });
        tf.SetFocus();
        app.Run(dlg);
        return dlg.Canceled ? null : tf.Text;
    }

    /// <summary>Multi-line input (share links, base64 blobs…).</summary>
    public static string? MultiLine(IApplication app, string title, string hint, string initial = "")
    {
        using var dlg = new Dialog { Title = title, Width = Dim.Percent(85), Height = Dim.Percent(70) };
#pragma warning disable CS0618 // TextView is obsolete in favour of an external editor package, but fine here.
        var tv = new TextView { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1), Text = initial, WordWrap = false };
#pragma warning restore CS0618
        dlg.Add(new Label { Text = hint, X = 1, Y = 1 }, tv);
        dlg.AddButton(new Button { Title = L("_Cancel", "_Отмена") });
        dlg.AddButton(new Button { Title = "_OK" });
        tv.SetFocus();
        app.Run(dlg);
        return dlg.Canceled ? null : tv.Text;
    }

    /// <summary>Pick one item from a list. Returns the index or null.</summary>
    public static int? Choose(IApplication app, string title, IReadOnlyList<string> items, int selected = 0)
    {
        var width = Math.Clamp(items.Select(s => s.Length).DefaultIfEmpty(10).Max() + 8, 30, 100);
        // Explicit list height + auto dialog height: Dim.Fill would not leave room for the buttons.
        using var dlg = new Dialog { Title = title, Width = width };
        var lv = new ListView { X = 1, Y = 1, Width = Dim.Fill(1), Height = Math.Clamp(items.Count, 1, Math.Max(3, app.Screen.Height - 10)) };
        lv.SetSource(new ObservableCollection<string>(items));
        if (items.Count > 0)
        {
            lv.SelectedItem = Math.Clamp(selected, 0, items.Count - 1);
        }
        dlg.Add(lv);
        dlg.AddButton(new Button { Title = L("_Cancel", "_Отмена") });
        dlg.AddButton(new Button { Title = "_OK" });
        lv.SetFocus();
        app.Run(dlg);
        return dlg.Canceled ? null : lv.SelectedItem;
    }

    /// <summary>Read-only text viewer with optional "copy" button.</summary>
    public static void ShowText(IApplication app, string title, string text, string? copyText = null)
    {
        var lines = text.Split('\n');
        var width = Math.Clamp(lines.Max(l => l.Length) + 6, 40, 200);
        using var dlg = new Dialog { Title = title, Width = Dim.Percent(95), Height = Dim.Percent(90) };
        dlg.Width = Dim.Func(_ => Math.Min(width, app.Screen.Width - 2));
#pragma warning disable CS0618
        var tv = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), Text = text, ReadOnly = true, WordWrap = false };
#pragma warning restore CS0618
        dlg.Add(tv);
        if (copyText != null)
        {
            var copy = new Button { Title = L("_Copy", "_Копировать") };
            copy.Accepting += (_, e) =>
            {
                e.Handled = true;
                Clip.Set(app, copyText);
            };
            dlg.AddButton(copy);
        }
        dlg.AddButton(new Button { Title = "_OK" });
        app.Run(dlg);
    }
}

/// <summary>Clipboard with a graceful fallback (no xclip / wl-copy → OSC 52 escape for the terminal).</summary>
internal static class Clip
{
    public static void Set(IApplication app, string text)
    {
        if (app.Clipboard is { IsSupported: true } cb && cb.TrySetClipboardData(text))
        {
            LogBus.Notice(Loc.T("Copied to clipboard", "Скопировано в буфер обмена"));
            return;
        }
        try
        {
            // OSC 52: most modern terminals (incl. over SSH / tmux with set-clipboard on) accept this.
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            Console.Out.Write($"\u001b]52;c;{b64}\u0007");
            Console.Out.Flush();
            LogBus.Notice(Loc.T("Copied via terminal (OSC 52)", "Скопировано через терминал (OSC 52)"));
        }
        catch
        {
            LogBus.Notice(Loc.T("Clipboard is not available", "Буфер обмена недоступен"));
        }
    }

    public static string? Get(IApplication app) =>
        app.Clipboard is { IsSupported: true } cb && cb.TryGetClipboardData(out var s) ? s : null;
}
