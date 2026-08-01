using System;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using SnapDoc.CaptureEngine;
using SnapDoc.Models;
using SnapDoc.Ocr;
using SnapDoc.Recording;
using SnapDoc.Services;
using SnapDoc.Views;

namespace SnapDoc;

/// <summary>
/// Composition root. Everything is wired here by hand (no DI container needed at this size).
/// To swap an implementation -- e.g. a real screen recorder, or a WGC capture engine --
/// change the one line here; nothing else in the app references concrete types.
/// </summary>
public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static ICaptureEngine CaptureEngine { get; private set; } = null!;
    public static IOcrEngine OcrEngine { get; private set; } = null!;
    public static IScreenRecorder Recorder { get; private set; } = null!;
    public static WorkspaceService Workspace { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static TrayIcon? Tray { get; private set; }

    /// <summary>Set by <see cref="RequestExit"/> before it calls Shutdown(). MainWindow's
    /// OnClosing override cancels a plain user-initiated close (to hide-to-tray instead) -- but
    /// Application.Shutdown() closes every open window as part of shutting down, and a *cancelled*
    /// Close() on any one of them aborts that whole sequence, silently leaving the process (and
    /// every other still-open window, e.g. the recording toolbar) running. Checking this flag is
    /// what lets MainWindow tell "the user clicked X" apart from "the app is exiting".</summary>
    public static bool IsExiting { get; private set; }

    private TrayIcon? _tray;

    // SnapDoc lives in the tray with no window shown on launch, so a user who isn't sure it
    // actually started (easy to miss a balloon tip) will naturally double-click the exe again --
    // and again. Without this guard, each click was launching a completely separate process,
    // each with its own tray icon and its own attempt to register the same global hotkeys --
    // exactly the "n number of installations" the user reported. No "Global\" prefix on the name,
    // so this is scoped to the current login session, not the whole machine -- one SnapDoc per
    // user, which is what "one tray icon" implies.
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "SnapDoc.SingleInstance.Mutex", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "SnapDoc is already running.\n\nCheck your system tray (near the clock) -- look for the SnapDoc icon, or press one of its capture hotkeys.",
                "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // QuestPDF community licence (required before generating any PDF).
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        Settings      = AppSettings.Load();
        CaptureEngine = new GdiCaptureEngine();               // swap for WgcCaptureEngine later
        OcrEngine     = new WindowsOcrEngine();               // swap/add TesseractOcrEngine later
        Recorder      = new MfScreenRecorder(CaptureEngine);  // Media Foundation SinkWriter, H.264
        Workspace     = new WorkspaceService(Settings);
        Hotkeys       = new HotkeyService();

        // Tray icon owns the app lifetime and the global hotkeys.
        _tray = new TrayIcon();
        Tray = _tray;
        _tray.Initialize();

        RegisterHotkeys();

        // Fire-and-forget: an update check should never delay startup or be able to crash it.
        // Silent when already up to date -- the "Check for updates…" tray item gives feedback
        // either way when the user asks explicitly.
        _ = TrayIcon.CheckForUpdatesOnStartup();
    }

    /// <summary>The one correct way to actually quit SnapDoc -- see <see cref="IsExiting"/>. Every
    /// exit path (tray menu, future "Exit" buttons, etc.) should call this instead of
    /// Application.Current.Shutdown() directly.</summary>
    public static void RequestExit()
    {
        IsExiting = true;
        // Belt-and-suspenders alongside the IsExiting flag: close the toolbar/countdown explicitly
        // rather than only relying on Shutdown()'s own window enumeration reaching them.
        CaptureController.CloseRecordingToolbarForShutdown();
        Current.Shutdown();
    }

    private void RegisterHotkeys()
    {
        Hotkeys.Register(Settings.HotkeyRegion,     () => CaptureController.StartRegionCapture());
        Hotkeys.Register(Settings.HotkeyFullScreen, () => CaptureController.CaptureCurrentMonitor());
        Hotkeys.Register(Settings.HotkeyWindow,     () => CaptureController.StartWindowCapture());
        Hotkeys.Register(Settings.HotkeyRecord,     () => CaptureController.ToggleRecording());
        Hotkeys.Register(Settings.HotkeyPauseResume, () => CaptureController.TogglePauseAsync());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Quitting mid-recording would otherwise leave the SinkWriter never Finalize()'d --
        // Finalize is what writes the MP4's moov atom, so skipping it produces an unplayable
        // file, not just a short one. Blocking briefly here to let it finish is worth that.
        if (Recorder?.IsRecording == true)
        {
            try { Recorder.StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        Hotkeys?.Dispose();
        _tray?.Dispose();
        Settings?.Save();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
