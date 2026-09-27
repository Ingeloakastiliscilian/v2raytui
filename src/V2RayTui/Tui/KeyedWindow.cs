using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace V2RayTui.Tui;

internal static class KeyMap
{
    // Hotkeys work on the Russian layout too: map by physical key position (ЙЦУКЕН → QWERTY).
    private const string CyrKeys = "йцукенгшщзхъфывапролджэячсмитьбюё";
    private const string LatKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";

    /// <summary>The printable character of a key press (layout-normalized), or null.</summary>
    public static char? Char(Key key)
    {
        if (key.IsCtrl || key.IsAlt)
        {
            return null;
        }
        var rune = key.AsRune;
        if (rune.Value == 0 || Rune.IsControl(rune) || rune.Utf16SequenceLength != 1)
        {
            return null;
        }
        var c = (char)rune.Value;
        var i = CyrKeys.IndexOf(char.ToLowerInvariant(c));
        if (i < 0)
        {
            return c;
        }
        var m = LatKeys[i];
        return char.IsUpper(c) ? char.ToUpperInvariant(m) : m;
    }
}

/// <summary>
/// A modal window with its own single-key commands. Keys are taken at application level (before the
/// focused list/table sees them), but only while this window is on top — dialogs opened from it are unaffected.
/// </summary>
internal abstract class KeyedWindow : Window
{
    private IApplication? _app;

    protected KeyedWindow()
    {
        X = Pos.Center();
        Y = Pos.Center();
        Width = Dim.Percent(94);
        Height = Dim.Percent(92);
    }

    protected static string L(string en, string ru) => Loc.T(en, ru);

    protected override void OnIsRunningChanged(bool newIsRunning)
    {
        base.OnIsRunningChanged(newIsRunning);
        if (newIsRunning && App is { } app)
        {
            _app = app;
            app.Keyboard.KeyDown += Hook;
            OnOpened();
        }
        else if (!newIsRunning && _app != null)
        {
            _app.Keyboard.KeyDown -= Hook;
            _app = null;
        }
    }

    private void Hook(object? sender, Key key)
    {
        if (key.Handled || _app?.TopRunnableView != this)
        {
            return;
        }
        if (OnKey(key))
        {
            key.Handled = true;
        }
    }

    protected virtual void OnOpened()
    {
    }

    /// <summary>Return true when the key was handled.</summary>
    protected abstract bool OnKey(Key key);

    protected void Close() => App?.RequestStop();

    protected Label HintLine(string text)
    {
        var l = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Text = text };
        Add(l);
        return l;
    }
}
