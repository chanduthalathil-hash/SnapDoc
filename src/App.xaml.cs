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
        CaptureEngine = new GdiCaptureEngine();              // swap for WgcCaptureEngine later
        OcrEngine     = new WindowsOcrEngine();              // swap/add TesseractOcrEngine later
        Recorder      = new StubScreenRecorder();            // swap for a real recorder later
        Workspace     = new WorkspaceService(Settings);
        Hotkeys       = new HotkeyService();

        // Tray icon owns the app lifetime and the global hotkeys.
        _tray = new TrayIcon();
        _tray.Initialize();

        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        Hotkeys.Register(Settings.HotkeyRegion,     () => CaptureController.StartRegionCapture());
        Hotkeys.Register(Settings.HotkeyFullScreen, () => CaptureController.CaptureCurrentMonitor());
        Hotkeys.Register(Settings.HotkeyWindow,     () => CaptureController.StartWindowCapture());
        // Record hotkey wired but StubScreenRecorder will show a friendly "not yet" message.
        Hotkeys.Register(Settings.HotkeyRecord,     () => CaptureController.ToggleRecording());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hotkeys?.Dispose();
        _tray?.Dispose();
        Settings?.Save();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
