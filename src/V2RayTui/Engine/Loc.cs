using System.Globalization;

namespace V2RayTui.Engine;

/// <summary>Tiny two-language helper for the TUI's own strings (engine messages come from ResUI).</summary>
public static class Loc
{
    public static bool IsRu { get; set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    public static string T(string en, string ru) => IsRu ? ru : en;
}
