using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapDoc.Models;
using SnapDoc.Recording;
using SnapDoc.Views;

namespace SnapDoc.Services;

/// <summary>
/// Central capture orchestration: called by hotkeys, tray menu, and main-window buttons.
/// Keeps the "what happens after a capture" policy in one place (clipboard, editor, OCR).
/// </summary>
public static class CaptureController
{
    // Tracks which of SnapDoc's own windows we hid for the current capture, so we only
    // restore the ones that were actually visible (and not, say, a Workspace the user had
    // deliberately closed earlier).
    private static readonly List<Window> _hiddenForCapture = new();

    // Guards against two capture cycles overlapping. HideAppWindows/RestoreAppWindows share
    // _hiddenForCapture; if a second Start*Capture() call ever ran while a first one's overlay
    // was still up (double hotkey fire, a stray click reaching a button/tray icon underneath the
    // overlay, etc.) the second call's HideAppWindows() would clear and rebuild that shared list
    // out from under the first, so RestoreAppWindows() -- or the second capture's own grab -- could
    // run against the wrong window state. Rather than assume this can't happen, make it impossible.
    private static bool _captureInProgress;

    private static bool TryEnterCapture()
    {
        if (_captureInProgress) return false;
        _captureInProgress = true;
        return true;
    }

    private static void ExitCapture() => _captureInProgress = false;

    /// <summary>
    /// Hide every currently-visible SnapDoc window before grabbing pixels.
    ///
    /// This used to ALSO call a raw Win32 ShowWindow(SW_HIDE) after w.Hide(), reasoning that a
    /// window shown moments earlier could still be mid-way through WPF's own show/layout pipeline
    /// when Hide() runs. That turned out to be the same mistake RestoreAppWindows' Win32 SW_SHOW
    /// call was (see its own doc comment): WPF's Window.Hide() already performs the equivalent
    /// Win32 call itself, through its own properly-tracked code path -- the explicit extra call was
    /// redundant, and mixing it with WPF's own state tracking on the SAME long-lived, tray-cached
    /// MainWindow instance across many hide/restore cycles is exactly what corrupts its HwndSource
    /// (the "root Visual of a VisualTarget cannot have a parent" crash in TrayIcon.ShowMain()).
    /// Removing the SW_SHOW half alone wasn't enough -- the SW_HIDE half was still doing the same
    /// kind of damage, just slower. The actual "wrong window captured" bug this was defending
    /// against was later found and fixed at its real source (CaptureOverlay's Closed-vs-
    /// RegionSelected event ordering); DwmFlush() in CaptureWithRetry is still there as a genuine
    /// compositor-timing backstop, so removing this redundant, risky Win32 call should be safe.
    /// </summary>
    private static void HideAppWindows()
    {
        _hiddenForCapture.Clear();
        foreach (Window w in Application.Current.Windows)
        {
            // The recording toolbar, its countdown, and the live webcam preview are all deliberately
            // excluded from capture via SetWindowDisplayAffinity (see CaptureExclusion) rather than
            // hidden -- that's what lets them stay visible/usable on screen throughout a recording
            // without ever appearing IN it. Hiding them here would make Stop unreachable mid-recording
            // (toolbar) and would defeat the whole point of the webcam preview (WebcamLivePreview) --
            // it's meant to keep showing exactly what's being composited into the file as it happens.
            if (w is RecordingToolbar or RecordingCountdown or WebcamLivePreview) continue;
            if (w.IsVisible)
            {
                _hiddenForCapture.Add(w);
                w.Hide();
            }
        }
    }

    /// <summary>
    /// WPF Show()/Hide() only now, on both the hide and restore side -- see HideAppWindows' doc
    /// comment for why the Win32-level calls this used to also make were removed. MainWindow is
    /// cached and reused by TrayIcon across a whole session's worth of hide/restore cycles, so
    /// anything that corrupts its window state doesn't fail immediately -- it fails later, on some
    /// unrelated subsequent Show(), which is exactly what made this so slow to pin down.
    /// </summary>
    private static void RestoreAppWindows()
    {
        foreach (var w in _hiddenForCapture)
            w.Show();
        _hiddenForCapture.Clear();
    }

    /// <summary>Blocks the calling thread until DWM (the desktop compositor) has actually
    /// composed a frame reflecting all pending window changes -- typically one frame, ~16ms at
    /// 60Hz. This is the correct, purpose-built primitive for "wait until the screen really
    /// reflects the window changes I just made" instead of guessing a fixed delay.</summary>
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    /// <summary>
    /// Grab pixels, after letting DWM actually finish compositing the desktop without SnapDoc's
    /// windows, then -- if the result STILL looks like a uniform blank frame -- wait and retry a
    /// couple more times.
    ///
    /// The wait matters even though HideAppWindows() already forces a synchronous Win32 hide:
    /// that updates each window's own visibility immediately, but DWM's recomposition of the
    /// desktop runs on its own thread and can lag a frame or two behind, especially right after a
    /// window was only just shown again by a *previous* capture's restore (rapid back-to-back
    /// captures). A stale composited frame that still shows SnapDoc's own window is real, valid,
    /// non-blank content -- it looks like a perfectly good capture -- so the blank-retry below
    /// cannot catch it. Only waiting for DWM before the first grab can. An earlier version used a
    /// guessed Task.Delay(150) here; DwmFlush waits for the actual compositor event instead of
    /// hoping a fixed delay was long enough.
    ///
    /// The flush runs via Task.Run (a background thread), then we await it, so the UI thread
    /// keeps pumping messages while it waits -- blocking the UI thread directly here previously
    /// made Windows treat SnapDoc as unresponsive mid-capture and paint over its windows, which
    /// was a worse bug than the one this works around.
    /// </summary>
    private static async Task<BitmapSource> CaptureWithRetry(Func<BitmapSource> grab)
    {
        // twice: give the hides two frames to land. Defensive try/catch: DWM composition being
        // unavailable shouldn't be able to break capture entirely, just lose this synchronization.
        await Task.Run(() => { try { DwmFlush(); DwmFlush(); } catch { } });
        var bmp = grab();
        for (int attempt = 0; attempt < 2 && LooksBlank(bmp); attempt++)
        {
            await Task.Delay(200);
            bmp = grab();
        }
        return bmp;
    }

    /// <summary>Cheap heuristic: sample a 3x3 grid and see if every sample is the same colour.
    /// A handful of genuinely blank captures exist (a plain white document), but those are rare
    /// next to compositor-race blanks, and this only ever triggers an extra retry, never a
    /// hard failure.</summary>
    private static bool LooksBlank(BitmapSource bmp)
    {
        try
        {
            var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            if (w < 3 || h < 3) return false;

            byte[]? reference = null;
            for (int i = 0; i < 9; i++)
            {
                int sx = Math.Clamp((int)((i % 3 + 0.5) * w / 3.0), 0, w - 1);
                int sy = Math.Clamp((int)((i / 3 + 0.5) * h / 3.0), 0, h - 1);
                var pixelSrc = new CroppedBitmap(converted, new Int32Rect(sx, sy, 1, 1));
                byte[] pixel = new byte[4];
                pixelSrc.CopyPixels(pixel, 4, 0);

                if (reference == null) { reference = pixel; continue; }
                if (pixel[0] != reference[0] || pixel[1] != reference[1] || pixel[2] != reference[2])
                    return false;
            }
            return true;
        }
        catch { return false; } // if sampling itself fails, don't block the capture on it
    }

    /// <summary>Show the drag-rectangle overlay; on confirm, capture and post-process.</summary>
    public static void StartRegionCapture()
    {
        if (!TryEnterCapture()) return;
        HideAppWindows();
        var overlay = new CaptureOverlay();
        overlay.RegionSelected += async physicalRect =>
        {
            var bmp = await CaptureWithRetry(() => App.CaptureEngine.CaptureRegion(physicalRect));
            RestoreAppWindows();
            ExitCapture();
            HandleNewCapture(bmp, "Region capture");
        };
        // Covers the cancel path too (Esc, or a too-small drag) -- otherwise SnapDoc's
        // windows would stay hidden with no capture ever taken. Checked via overlay.SelectionMade
        // (set synchronously before Close()), NOT a flag set inside RegionSelected -- Closed fires
        // synchronously from Close(), one dispatcher tick before RegionSelected does, so a flag set
        // only inside RegionSelected would still read false here even on a real selection. That was
        // the actual bug: this used to restore SnapDoc's windows before the grab ran on every
        // successful capture, not just on a real cancel.
        overlay.Closed += (_, _) => { if (!overlay.SelectionMade) { RestoreAppWindows(); ExitCapture(); } };
        overlay.Show();
    }

    public static async void CaptureCurrentMonitor()
    {
        if (!TryEnterCapture()) return;
        HideAppWindows();
        var bmp = await CaptureWithRetry(() => App.CaptureEngine.CaptureCurrentMonitor());
        RestoreAppWindows();
        ExitCapture();
        HandleNewCapture(bmp, "Monitor capture");
    }

    /// <summary>Window pick: reuse the overlay in "click a window" mode (see CaptureOverlay).</summary>
    public static void StartWindowCapture()
    {
        if (!TryEnterCapture()) return;
        HideAppWindows();
        var overlay = new CaptureOverlay { WindowPickMode = true };
        overlay.WindowPicked += async hwnd =>
        {
            var bmp = await CaptureWithRetry(() => App.CaptureEngine.CaptureWindow(hwnd));
            RestoreAppWindows();
            ExitCapture();
            HandleNewCapture(bmp, "Window capture");
        };
        overlay.Closed += (_, _) => { if (!overlay.SelectionMade) { RestoreAppWindows(); ExitCapture(); } };
        overlay.Show();
    }

    // ==================== Screen recording ====================
    //
    // The recording toolbar (Views/RecordingToolbar) is the primary UI for this -- source/mic/
    // system-audio/webcam pickers, Start, and (once recording) timer/pause/mute/Stop. Everything
    // below is orchestration the toolbar calls into, and which the global hotkeys (Ctrl+Shift+R
    // start/stop, Ctrl+Shift+P pause/resume) call into too, so a hotkey-triggered change and a
    // toolbar-click-triggered one both go through the exact same path and the toolbar (if open)
    // stays in sync regardless of which one the user used.

    private static RecordingToolbar? _toolbar;
    private static string? _recordingPath;
    private static DateTime _recordingStartedAt;

    // What a bare Ctrl+Shift+R (no toolbar interaction first) starts with: whatever was last
    // configured in the toolbar, or Full Screen/no audio the very first time.
    private static Int32Rect? _lastRegion;
    private static RecordingOptions _lastOptions = new();

    private static RecordingToolbar EnsureToolbar()
    {
        if (_toolbar == null || !_toolbar.IsLoaded)
            _toolbar = new RecordingToolbar();
        return _toolbar;
    }

    /// <summary>Called from App.RequestExit -- force-closes the toolbar regardless of its normal
    /// "don't disappear mid-recording" guard, since App.OnExit already stops any in-progress
    /// recording before the process actually exits.</summary>
    public static void CloseRecordingToolbarForShutdown()
    {
        try { _toolbar?.Close(); } catch { /* already closing/closed */ }
        _toolbar = null;
    }

    /// <summary>Called from MainWindow.OnClosing. Closing the workspace window is how most users
    /// expect to "close the app" -- even though SnapDoc actually just keeps running in the tray --
    /// so a recording toolbar left idle in Setup state (nothing to Start yet, from some earlier
    /// visit) shouldn't stay floating on screen behind what looks like a closed app. Left alone
    /// while a recording is actually running, since Stop/Pause need to stay reachable.</summary>
    public static void HideIdleRecordingToolbar()
    {
        if (App.Recorder.IsRecording) return;
        _toolbar?.HideForIdle();
    }

    /// <summary>Opens the recorder's control bar (Setup state if idle, Recording state if a
    /// recording -- started via hotkey, say -- is already running). Used by the tray menu, the
    /// main window's Record button, etc.; does not itself start anything.</summary>
    public static void ShowRecordingToolbar()
    {
        var toolbar = EnsureToolbar();
        if (App.Recorder.IsRecording) toolbar.ShowRecordingState(_lastOptions);
        else toolbar.ShowSetupState();
        toolbar.Show();
        toolbar.Activate();
    }

    /// <summary>Drives the toolbar's "Region" source button: hide the toolbar, reuse the same
    /// drag-select overlay a screenshot uses, hand back physical-pixel bounds.</summary>
    public static void PickRegionForRecording(Action<Int32Rect> onPicked, Action onCancelled)
    {
        var overlay = new CaptureOverlay();
        overlay.RegionSelected += rect => onPicked(rect);
        overlay.Closed += (_, _) => { if (!overlay.SelectionMade) onCancelled(); };
        overlay.Show();
    }

    /// <summary>Drives the toolbar's "Window" source button.</summary>
    public static void PickWindowForRecording(Action<Int32Rect> onPicked, Action onCancelled)
    {
        var overlay = new CaptureOverlay { WindowPickMode = true };
        overlay.WindowPicked += hwnd => onPicked(App.CaptureEngine.GetWindowBounds(hwnd));
        overlay.Closed += (_, _) => { if (!overlay.SelectionMade) onCancelled(); };
        overlay.Show();
    }

    /// <summary>Ctrl+Shift+R: start with the last-configured source/options if idle, stop if not.</summary>
    public static void ToggleRecording()
    {
        if (App.Recorder.IsRecording) _ = StopRecordingAsync();
        else _ = StartRecordingAsync(_lastRegion ?? App.CaptureEngine.GetCurrentMonitorBounds(), _lastOptions);
    }

    public static async void TogglePauseAsync()
    {
        if (!App.Recorder.IsRecording) return;
        if (App.Recorder.IsPaused) await App.Recorder.ResumeAsync();
        else await App.Recorder.PauseAsync();
        _toolbar?.RefreshPauseState();
    }

    /// <summary>The actual start: a 3-second countdown over the chosen region, then hide SnapDoc's
    /// own windows (same TryEnterCapture/HideAppWindows machinery a screenshot uses, so a
    /// screenshot hotkey can't fire mid-recording and corrupt the shared hide/restore state) and
    /// hand off to the recorder. Called by the toolbar's Start button and by the hotkey alike.</summary>
    public static async Task StartRecordingAsync(Int32Rect region, RecordingOptions options)
    {
        if (!TryEnterCapture())
        {
            MessageBox.Show("Finish the current capture first.", "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _lastRegion = region;
        _lastOptions = options;

        var toolbar = EnsureToolbar();
        toolbar.Hide(); // out of the way during the countdown -- nothing to control yet

        var countdownDone = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var countdown = new RecordingCountdown(region);
        countdown.Finished += () => countdownDone.SetResult(null);
        countdown.Start();
        await countdownDone.Task;

        HideAppWindows();
        await Task.Run(() => { try { DwmFlush(); DwmFlush(); } catch { } });

        try
        {
            string path = System.IO.Path.Combine(App.Settings.WorkspaceFolder,
                $"SnapDoc Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");

            _recordingStartedAt = DateTime.Now;
            await App.Recorder.StartAsync(region, path, options);
            _recordingPath = path;

            App.Tray?.SetRecordingIndicator(true);
            toolbar.ShowRecordingState(options);
            toolbar.Show();
        }
        catch (Exception ex)
        {
            RestoreAppWindows();
            ExitCapture();
            MessageBox.Show($"Couldn't start recording:\n{ex.Message}", "SnapDoc — Recording", MessageBoxButton.OK, MessageBoxImage.Error);
            toolbar.ShowSetupState();
            toolbar.Show();
        }
    }

    public static async Task StopRecordingAsync()
    {
        try
        {
            await App.Recorder.StopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Recording finished with an error:\n{ex.Message}", "SnapDoc — Recording", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RestoreAppWindows();
            ExitCapture();
            App.Tray?.SetRecordingIndicator(false);
        }

        if (_recordingPath != null && System.IO.File.Exists(_recordingPath))
        {
            App.Workspace.AddRecording(_recordingPath, DateTime.Now - _recordingStartedAt);
        }
        _recordingPath = null;

        _toolbar?.ResetAndHideAfterStop();
    }

    private static async void HandleNewCapture(BitmapSource bmp, string captureKind)
    {
        var capture = new Capture { Image = bmp, CaptureKind = captureKind };

        if (App.Settings.CopyToClipboardOnCapture)
        {
            try { Clipboard.SetImage(bmp); } catch { /* clipboard can transiently fail; ignore */ }
        }

        App.Workspace.Add(capture);

        // Save to disk right away, like a normal screenshot tool -- don't make the file's
        // existence depend on the user remembering to hit "Save PNG" in the editor.
        try { App.Workspace.SavePng(capture); } catch { /* non-fatal: capture still lives in the workspace list */ }

        if (App.Settings.RunOcrAutomatically && App.OcrEngine.IsAvailable)
        {
            try { capture.OcrText = await App.OcrEngine.RecognizeAsync(bmp); } catch { }
        }

        if (App.Settings.OpenEditorOnCapture)
        {
            var editor = new EditorWindow(capture);
            editor.Show();
            editor.Activate();
            // This window is shown right after SnapDoc restores its own hidden windows -- force
            // its layout/render pass to actually run now instead of waiting for the next natural
            // tick, which is where a blank-first-frame render glitch has been reported.
            editor.UpdateLayout();
        }
    }
}
