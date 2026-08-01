using System;
using System.Threading.Tasks;
using Velopack;
using SnapDoc.Services;

namespace SnapDoc;

public static class Program
{
    // Velopack needs to be the very first thing that runs, before any WPF/app startup code --
    // when Windows launches this exe for an install/update/uninstall lifecycle event (not a
    // normal user launch), Velopack detects that from the process args here and runs its own
    // one-time hook (e.g. creating the Start Menu shortcut on first install) then exits the
    // process immediately, never reaching the rest of Main. That's why this can't live inside
    // App.OnStartup -- by the time WPF's own startup runs, it would already be too late.
    [STAThread]
    public static void Main(string[] args)
    {
        // See CrashLogger's doc comment: this is a no-window tray app, so without these hooks a
        // startup failure (including one inside VelopackApp.Build().Run() itself, e.g. right after
        // an auto-update relaunches the process) had nothing to show it ever happened.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLogger.LogAndShow(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.LogAndShow(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        try
        {
            VelopackApp.Build().Run();

            var app = new App();
            app.DispatcherUnhandledException += (_, e) =>
            {
                CrashLogger.LogAndShow(e.Exception, "DispatcherUnhandledException");
                e.Handled = true; // a tray app should survive one bad event, not vanish over it
            };
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            CrashLogger.LogAndShow(ex, "Main");
        }
    }
}
