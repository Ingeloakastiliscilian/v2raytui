using Terminal.Gui.App;

namespace V2RayTui.Tui;

public enum TuiExit
{
    Quit,
    Detach,
}

public static class TuiApp
{
    public static TuiExit Run()
    {
        using var app = Application.Create().Init();
        Theme.Install();
        using var main = new MainWindow();
        app.Run(main);
        return main.ExitMode;
    }
}
