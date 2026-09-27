using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;

namespace V2RayTui.Tui;

/// <summary>
/// The TUI's palette (soft colours on top of the terminal's own background, Catppuccin-like)
/// and the schemes built from it.
/// </summary>
internal static class Theme
{
    public static readonly Color None = Color.None;
    public static readonly Color Text = new(205, 214, 244);
    public static readonly Color Sub = new(166, 173, 200);
    public static readonly Color Dim = new(108, 112, 134);
    public static readonly Color Surface = new(49, 50, 68);
    public static readonly Color Surface2 = new(69, 71, 90);
    public static readonly Color Crust = new(24, 24, 37);
    public static readonly Color Blue = new(137, 180, 250);
    public static readonly Color Mauve = new(203, 166, 247);
    public static readonly Color Green = new(166, 227, 161);
    public static readonly Color Yellow = new(249, 226, 175);
    public static readonly Color Peach = new(250, 179, 135);
    public static readonly Color Red = new(243, 139, 168);
    public static readonly Color Teal = new(148, 226, 213);
    public static readonly Color MismatchBg = new(92, 42, 54);

    public static Attribute A(Color fg, Color? bg = null, TextStyle style = TextStyle.None) => new(fg, bg ?? None, style);

    /// <summary>Base scheme for the main window and its lists / tables.</summary>
    public static Scheme Base { get; } = new(A(Text))
    {
        Focus = A(Text, Surface2, TextStyle.Bold),
        HotNormal = A(Blue),
        HotFocus = A(Blue, Surface2, TextStyle.Bold),
        Active = A(Text, Surface),
        HotActive = A(Blue, Surface),
        Highlight = A(Text, Surface),
        Disabled = A(Dim),
        Editable = A(Text),
        ReadOnly = A(Sub),
    };

    public static Scheme Dialog { get; } = new(A(Text, Surface))
    {
        Focus = A(Crust, Blue, TextStyle.Bold),
        HotNormal = A(Blue, Surface, TextStyle.Bold),
        HotFocus = A(Crust, Blue, TextStyle.Bold),
        Active = A(Text, Surface2),
        HotActive = A(Blue, Surface2, TextStyle.Bold),
        Highlight = A(Text, Surface2),
        Editable = A(Text, Surface2),
        ReadOnly = A(Sub, Surface),
        Disabled = A(Dim, Surface),
    };

    public static Scheme Error { get; } = new(A(Text, new Color(88, 36, 48)))
    {
        Focus = A(Crust, Red, TextStyle.Bold),
        HotNormal = A(Red, new Color(88, 36, 48), TextStyle.Bold),
        HotFocus = A(Crust, Red, TextStyle.Bold),
        Active = A(Text, new Color(110, 46, 60)),
        Highlight = A(Text, new Color(110, 46, 60)),
        Editable = A(Text, new Color(110, 46, 60)),
        ReadOnly = A(Sub, new Color(88, 36, 48)),
        Disabled = A(Dim, new Color(88, 36, 48)),
    };

    /// <summary>QR codes must stay black on white for cameras, whatever the theme.</summary>
    public static Scheme Qr { get; } = Flat(A(new Color(0, 0, 0), new Color(255, 255, 255)));

    /// <summary>Replaces Terminal.Gui's built-in schemes, so every window (incl. MessageBox) uses the palette.</summary>
    public static void Install()
    {
        SchemeManager.AddScheme("Base", Base);
        SchemeManager.AddScheme("Accent", Base);
        SchemeManager.AddScheme("Dialog", Dialog);
        SchemeManager.AddScheme("Menu", ShortcutBar);
        SchemeManager.AddScheme("Error", Error);
    }

    /// <summary>Same attribute for every role (headers, bars: nothing should change on focus).</summary>
    public static Scheme Flat(Attribute a) => new(a)
    {
        Focus = a, HotNormal = a, HotFocus = a, Active = a, HotActive = a, Highlight = a, Editable = a, ReadOnly = a, Disabled = a,
    };

    /// <summary>Bottom shortcuts bar: subtle surface, keys in accent.</summary>
    public static Scheme ShortcutBar { get; } = new(A(Sub, Surface))
    {
        Focus = A(Text, Surface2), HotNormal = A(Blue, Surface, TextStyle.Bold), HotFocus = A(Blue, Surface2, TextStyle.Bold),
        Active = A(Sub, Surface), HotActive = A(Blue, Surface, TextStyle.Bold), Highlight = A(Text, Surface2),
    };

    private static Scheme BorderScheme(bool focused) => new(A(focused ? Blue : Dim))
    {
        Focus = A(Blue, None, TextStyle.Bold),
        HotFocus = A(Blue, None, TextStyle.Bold),
        HotNormal = A(Sub),
        Highlight = A(Blue),
    };

    /// <summary>Rounded border; the border lights up while the panel has focus.</summary>
    public static void Panel(FrameView frame)
    {
        frame.BorderStyle = LineStyle.Rounded;
        frame.SetScheme(Base);
        var border = frame.Border.GetOrCreateView();
        border.SetScheme(BorderScheme(false));
        frame.HasFocusChanged += (_, e) =>
        {
            border.SetScheme(BorderScheme(e.NewValue));
            border.SetNeedsDraw();
        };
    }

    public static Color DelayColor(int delay) => delay switch
    {
        < 0 => Red,
        < 300 => Green,
        < 800 => Yellow,
        _ => Peach,
    };

    public static Color SpeedColor(decimal speed) => speed switch
    {
        <= 0 => Dim,
        < 1 => Peach,
        < 5 => Yellow,
        _ => Green,
    };

    /// <summary>"▰▰▰▱▱" — share of <paramref name="value"/> in <paramref name="max"/>.</summary>
    public static string Bar(decimal value, decimal max, int cells = 5)
    {
        if (max <= 0 || value <= 0)
        {
            return new string('▱', cells);
        }
        var filled = (int)Math.Clamp(Math.Round(value / max * cells), 1, cells);
        return new string('▰', filled) + new string('▱', cells - filled);
    }

    public static string ProtocolShort(EConfigType t) => t switch
    {
        EConfigType.Shadowsocks => "SS",
        EConfigType.Hysteria2 => "Hy2",
        EConfigType.WireGuard => "WG",
        EConfigType.Anytls => "AnyTLS",
        _ => t.ToString(),
    };
}

/// <summary>A one-line bar of coloured segments: left-aligned ones, then right-aligned ones.</summary>
internal sealed class SegmentBar : View
{
    public readonly record struct Segment(string Text, Attribute Attr);

    private IReadOnlyList<Segment> _left = [];
    private IReadOnlyList<Segment> _right = [];
    private readonly Color _background;

    public SegmentBar(Color background)
    {
        _background = background;
        Height = 1;
        Width = Dim.Fill();
        CanFocus = false;
    }

    public void Set(IReadOnlyList<Segment> left, IReadOnlyList<Segment>? right = null)
    {
        _left = left;
        _right = right ?? [];
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        SetAttribute(Theme.A(Theme.Text, _background));
        Move(0, 0);
        AddStr(new string(' ', Math.Max(0, width)));

        var rightWidth = _right.Sum(s => s.Text.GetColumns());
        var x = 0;
        foreach (var seg in _left)
        {
            var room = width - rightWidth - x;
            if (room <= 0)
            {
                break;
            }
            var text = Fit(seg.Text, room);
            Draw(ref x, text, seg.Attr);
        }
        x = Math.Max(x, width - rightWidth);
        foreach (var seg in _right)
        {
            if (x >= width)
            {
                break;
            }
            Draw(ref x, Fit(seg.Text, width - x), seg.Attr);
        }
        return true;
    }

    private void Draw(ref int x, string text, Attribute attr)
    {
        var a = attr.Background == Theme.None ? attr with { Background = _background } : attr;
        SetAttribute(a);
        Move(x, 0);
        AddStr(text);
        x += text.GetColumns();
    }

    private static string Fit(string text, int room)
    {
        if (text.GetColumns() <= room)
        {
            return text;
        }
        var sb = new StringBuilder();
        foreach (var r in text.EnumerateRunes())
        {
            if (sb.ToString().GetColumns() + 2 > room)
            {
                break;
            }
            sb.Append(r.ToString());
        }
        return sb + "…";
    }
}
