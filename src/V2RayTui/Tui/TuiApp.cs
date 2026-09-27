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
    private static readonly Lock _gate = new();
    private static IApplication? _app;
    private static MainWindow? _main;

    /// <summary>Runs the interface on the current stdin/stdout terminal until the user leaves it.</summary>
    public static TuiExit Run()
    {
        using var app = Application.Create().Init();
        Theme.Install();
        using var main = new MainWindow();
        lock (_gate)
        {
            _app = app;
            _main = main;
        }
        try
        {
            app.Run(main);
        }
        finally
        {
            lock (_gate)
            {
                _app = null;
                _main = null;
            }
        }
        return main.ExitMode;
    }

    /// <summary>
    /// Closes the interface from outside (another terminal took it over, or its terminal went away),
    /// as "Background": the proxy keeps running.
    /// </summary>
    public static void ForceDetach()
    {
        IApplication? app;
        MainWindow? main;
        lock (_gate)
        {
            app = _app;
            main = _main;
        }
        if (app is null || main is null)
        {
            return;
        }
        main.SetExitMode(TuiExit.Detach);
        app.Invoke(() => StopAll(app, main));
    }

    // Modal dialogs run nested loops: stop them one by one until the main window is gone.
    private static void StopAll(IApplication app, MainWindow main)
    {
        if (!main.IsRunning)
        {
            return;
        }
        app.RequestStop();
        app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            if (main.IsRunning)
            {
                app.RequestStop();
            }
            return main.IsRunning;
        });
    }
}
