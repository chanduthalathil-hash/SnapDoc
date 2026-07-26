using System;
using Velopack;

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
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
