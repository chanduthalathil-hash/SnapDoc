using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using SnapDoc.Services;
using Forms = System.Windows.Forms;

namespace SnapDoc.Views;

/// <summary>
/// The always-present system-tray presence. Owns the app lifetime: the process runs headless in
/// the tray and only shows windows on demand. Uses WinForms NotifyIcon (WPF has no native tray API).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private Forms.NotifyIcon? _icon;
    private MainWindow? _main;
    private bool _showingMain;
    private Forms.ToolStripMenuItem? _restartToUpdateItem;

    public void Initialize()
    {
        _icon = new Forms.NotifyIcon
        {
            Text = "SnapDoc — screen capture & docs",
            Visible = true,
            Icon = LoadAppIcon(),
        };

        var menu = new Forms.ContextMenuStrip();

        // Hidden until an update has actually finished downloading -- see UpdateService.UpdateReady.
        _restartToUpdateItem = new Forms.ToolStripMenuItem("Restart to update", null,
            (_, _) => UpdateService.ApplyUpdateAndRestart()) { Visible = false, Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        menu.Items.Add(_restartToUpdateItem);
        menu.Items.Add(new Forms.ToolStripSeparator() { Visible = false, Tag = "update-separator" });

        menu.Items.Add("Capture region\tCtrl+Shift+1", null, (_, _) => CaptureController.StartRegionCapture());
        menu.Items.Add("Capture monitor\tCtrl+Shift+2", null, (_, _) => CaptureController.CaptureCurrentMonitor());
        menu.Items.Add("Capture window\tCtrl+Shift+3", null, (_, _) => CaptureController.StartWindowCapture());
        menu.Items.Add("Open recording toolbar", null, (_, _) => CaptureController.ShowRecordingToolbar());
        menu.Items.Add("Start/stop recording\tCtrl+Shift+R", null, (_, _) => CaptureController.ToggleRecording());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open workspace…", null, (_, _) => ShowMain());
        menu.Items.Add("Settings…", null, (_, _) => ShowMain());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Check for updates…", null, async (_, _) => await CheckForUpdatesManually());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => App.RequestExit());
        _icon.ContextMenuStrip = menu;

        _icon.DoubleClick += (_, _) => ShowMain();
        // Left-click also opens the workspace: icons parked in the taskbar's hidden-icons
        // overflow flyout don't reliably deliver DoubleClick, only single clicks.
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowMain();
        };

        _icon.ShowBalloonTip(2000, "SnapDoc is running",
            "Press Ctrl+Shift+1 to capture a region.", Forms.ToolTipIcon.Info);

        // Only acts if it's actually the "update ready" balloon being clicked -- the plain
        // "SnapDoc is running" balloon above shares the same event and shouldn't restart anything.
        _icon.BalloonTipClicked += (_, _) => { if (UpdateService.IsUpdateReady) UpdateService.ApplyUpdateAndRestart(); };

        UpdateService.UpdateReady += OnUpdateReady;
    }

    /// <summary>Balloon feedback for things that happen with no window open, like a recording
    /// starting/stopping while the app lives only in the tray.</summary>
    public void ShowBalloon(string title, string text) =>
        _icon?.ShowBalloonTip(2500, title, text, Forms.ToolTipIcon.Info);

    /// <summary>Swap the tray tooltip while a recording is in progress -- the only always-visible
    /// indicator that SnapDoc is currently capturing video, since its windows are hidden for the
    /// duration (see CaptureController.StartRecordingAsync).</summary>
    public void SetRecordingIndicator(bool recording)
    {
        if (_icon != null)
            _icon.Text = recording ? "SnapDoc — Recording… (Ctrl+Shift+R to stop)" : "SnapDoc — screen capture & docs";
    }

    // Runs once on startup (from App.OnStartup) and again any time the user picks "Check for
    // updates…" from the tray menu -- the only difference is a manual check tells you when
    // you're already up to date; the silent startup one doesn't bother you with that.
    public static async Task CheckForUpdatesOnStartup() => await UpdateService.CheckForUpdatesAsync();

    private async Task CheckForUpdatesManually()
    {
        string? version = await UpdateService.CheckForUpdatesAsync();
        if (version == null)
            _icon?.ShowBalloonTip(2000, "SnapDoc", "You're already on the latest version.", Forms.ToolTipIcon.Info);
    }

    // UpdateService.CheckForUpdatesAsync() is awaited without ConfigureAwait(false) anywhere in
    // its chain, so this continuation resumes on the same UI thread that created it (WPF's
    // Dispatcher acts as the SynchronizationContext) -- safe to touch the WinForms tray controls
    // directly here, no manual Invoke/marshaling needed.
    private void OnUpdateReady(string version)
    {
        if (_restartToUpdateItem == null || _icon?.ContextMenuStrip == null) return;
        _restartToUpdateItem.Text = $"Restart to update (v{version})";
        _restartToUpdateItem.Visible = true;
        foreach (Forms.ToolStripItem item in _icon.ContextMenuStrip.Items)
            if (item is Forms.ToolStripSeparator && (string?)item.Tag == "update-separator")
                item.Visible = true;

        _icon.ShowBalloonTip(4000, "SnapDoc update ready",
            $"Version {version} has been downloaded. Click here or use the tray menu to restart and update.",
            Forms.ToolTipIcon.Info);
    }

    // Reads the same Assets/icon.ico embedded as a WPF pack resource (see SnapDoc.csproj's
    // ApplicationIcon/Resource entries) rather than shipping a second copy of the icon file, so
    // the exe icon, window title bars, and the tray icon all stay in sync from one source.
    private static Icon LoadAppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"));
            if (info != null) return new Icon(info.Stream);
        }
        catch { /* fall back to a generic icon rather than fail startup over branding */ }
        return SystemIcons.Application;
    }

    // A single physical double-click on the tray icon delivers TWO separate WM_LBUTTONUP
    // messages (one per press-release), each raising MouseClick, plus a DoubleClick on top --
    // so ShowMain() can be entered twice in quick succession from one double-click alone, with
    // no DoubleClick handler even involved. Window.Show() pumps the Win32 message queue while it
    // builds the window's HwndSource, so that second, near-simultaneous call could run
    // re-entrantly on the SAME _main instance *while the first Show() call was still mid-flight*
    // -- calling Show() again on a Window before its previous Show() has finished setting its
    // root Visual is exactly what throws "The root Visual of a VisualTarget cannot have a
    // parent." The guard below is safe with a plain bool (no lock) because the re-entrant call
    // and the original call both run on the same UI thread.
    private void ShowMain()
    {
        if (_showingMain) return;
        _showingMain = true;
        try
        {
            _main ??= new MainWindow();
            _main.Show();
            _main.WindowState = WindowState.Normal;
            _main.Activate();
        }
        finally { _showingMain = false; }
    }

    public void Dispose()
    {
        UpdateService.UpdateReady -= OnUpdateReady;
        if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; }
    }
}
