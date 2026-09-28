using Terminal.Gui.App;

namespace V2RayTui.Tui;

public enum TuiExit
{
    /// <summary>"Quit": stop everything.</summary>
    Quit,

    /// <summary>"Background": close the window, keep the proxy and background work running.</summary>
    Detach,
}

public static class TuiApp
{
    private static volatile MainWindow? _main;

    /// <summary>
    /// Runs the interface on the current stdin/stdout terminal until the user leaves it, or until
    /// <paramref name="detachWhen"/> turns true (polled on the UI thread; closes it as "Background").
    /// </summary>
    public static TuiExit Run(Func<bool>? detachWhen = null)
    {
        using var app = Application.Create().Init();
        Theme.Install();
        using var main = new MainWindow { DetachWhen = detachWhen };
        _main = main;
        try
        {
            app.Run(main);
        }
        finally
        {
            _main = null;
        }
        return main.ExitMode;
    }

    /// <summary>
    /// Closes the interface from outside (another terminal took it over, or its terminal went away),
    /// as "Background": the proxy keeps running. Safe from any thread: only sets a flag that the UI thread
    /// polls — calling into Terminal.Gui from here could block while a modal dialog is open.
    /// </summary>
    public static void ForceDetach() => _main?.RequestDetach();
}
