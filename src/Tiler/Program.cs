using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Tiler;

internal static class Program
{
    const string InstanceMutex = @"Local\Tiler.SingleInstance";

    /// <summary>
    /// Command line:
    ///   --no-elevate           run without asking for administrator rights (windows of elevated apps are then out of reach).
    ///   --only-title PREFIX    development sandbox: tile only windows whose title starts with PREFIX;
    ///                          may run next to a normal instance.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        int filterAt = Array.IndexOf(args, "--only-title");
        if (filterAt >= 0 && filterAt + 1 < args.Length)
            WindowOps.TitleFilter = args[filterAt + 1];
        bool sandbox = WindowOps.TitleFilter != null;

        if (!sandbox && IsAlreadyRunning())
            return 0;

        bool elevated = Elevation.IsElevated();
        if (!elevated && !args.Contains("--no-elevate") && Elevation.TryRelaunchElevated(args))
            return 0;

        Mutex? mutex = null;
        if (!sandbox)
        {
            mutex = new Mutex(true, InstanceMutex, out bool created);
            if (!created)
            {
                mutex.Dispose();
                return 0;
            }
        }
        using var ownedMutex = mutex;

        Log.Rotate();
        Log.Info($"Starting, elevated={elevated}, title filter={WindowOps.TitleFilter ?? "none"}");

        System.Windows.Forms.Application.EnableVisualStyles();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
        };

        var settings = Settings.Load();
        using var controller = new TilerController(settings);
        using var tray = new TrayIcon(settings, controller, elevated, app.Shutdown);
        app.Run();

        Log.Info("Stopped");
        return 0;
    }

    static bool IsAlreadyRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(InstanceMutex, out var existing))
                return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex belongs to an elevated instance.
            return true;
        }
    }
}
