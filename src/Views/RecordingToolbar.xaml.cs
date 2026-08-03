using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SnapDoc.Recording;
using SnapDoc.Services;

namespace SnapDoc.Views;

/// <summary>
/// Compact floating recording control bar. Setup state (source/mic/system-audio/webcam pickers +
/// Start) and Recording state (timer/pause/mute/Stop) share one window, toggled by swapping which
/// of <see cref="SetupPanel"/>/<see cref="RecordingPanel"/> is visible -- simpler than two windows
/// and keeps position/drag state naturally continuous across Start/Stop.
///
/// This window is deliberately dumb about *whether* a recording is allowed to start/stop right
/// now -- all of that orchestration (the countdown, hiding the main window, actually calling the
/// recorder) lives in <see cref="CaptureController"/>, which is also what the global hotkeys drive.
/// The toolbar just calls into it and reflects the result, so a hotkey-triggered start/stop stays
/// in sync with this window even though it didn't originate here.
/// </summary>
public partial class RecordingToolbar : Window
{
    private Int32Rect? _selectedRegion;

    private bool _micOn;
    private string? _micDeviceId;
    private bool _systemAudioOn;
    private bool _webcamOn;
    private string? _webcamDeviceId;

    private int _frameRate = 12;
    private bool _drawCursor = true;
    private double _webcamAnchorX = 1.0;
    private double _webcamAnchorY = 1.0;
    private double _webcamSizeFraction = 0.2;

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // The live "what your webcam will look like" preview -- shown whenever webcam is toggled on
    // in Setup state. Owns the WebcamCapture instance (not the preview window itself) because it
    // has to be disposed to free the camera before the real recording opens the same device.
    private WebcamCapture? _previewCapture;
    private WebcamLivePreview? _previewWindow;

    public RecordingToolbar()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            PositionBottomCenter();
            CaptureExclusion.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        };
        Closed += (_, _) => StopWebcamPreview();

        _clockTimer.Tick += (_, _) => TimerText.Text = App.Recorder.Elapsed.ToString(@"hh\:mm\:ss");
    }

    private void PositionBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 24;
    }

    private void DragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    // ---- setup state: source selection ----

    private void SourceFullScreenBtn_Checked(object sender, RoutedEventArgs e)
    {
        _selectedRegion = App.CaptureEngine.GetCurrentMonitorBounds();
        _previewWindow?.UpdateRegion(_selectedRegion.Value);
    }

    private void SourceRegionBtn_Checked(object sender, RoutedEventArgs e)
    {
        Hide();
        CaptureController.PickRegionForRecording(
            region => { _selectedRegion = region; _previewWindow?.UpdateRegion(region); Show(); },
            () => { SourceFullScreenBtn.IsChecked = true; Show(); }); // cancelled -- fall back to Full Screen
    }

    private void SourceWindowBtn_Checked(object sender, RoutedEventArgs e)
    {
        Hide();
        CaptureController.PickWindowForRecording(
            region => { _selectedRegion = region; _previewWindow?.UpdateRegion(region); Show(); },
            () => { SourceFullScreenBtn.IsChecked = true; Show(); });
    }

    // ---- setup state: audio ----

    private void MicToggle_Click(object sender, RoutedEventArgs e) => _micOn = MicToggle.IsChecked == true;

    private void MicPickerBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var devices = AudioDevices.ListMicrophones();
        if (devices.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No microphones found", IsEnabled = false });
        }
        else
        {
            var systemDefault = new MenuItem { Header = "System default" };
            systemDefault.Click += (_, _) => { _micDeviceId = null; _micOn = true; MicToggle.IsChecked = true; };
            menu.Items.Add(systemDefault);
            foreach (var device in devices)
            {
                var item = new MenuItem { Header = device.Name };
                item.Click += (_, _) => { _micDeviceId = device.Id; _micOn = true; MicToggle.IsChecked = true; };
                menu.Items.Add(item);
            }
        }
        menu.PlacementTarget = MicPickerBtn;
        menu.IsOpen = true;
    }

    private void SystemAudioToggle_Click(object sender, RoutedEventArgs e) => _systemAudioOn = SystemAudioToggle.IsChecked == true;

    // ---- setup state: webcam ----

    private void WebcamToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WebcamToggle.IsChecked != true)
        {
            _webcamOn = false;
            StopWebcamPreview();
            return;
        }

        if (_webcamDeviceId == null)
        {
            var first = VideoDevices.ListCameras().FirstOrDefault();
            if (first == null)
            {
                MessageBox.Show("No camera found.", "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Information);
                WebcamToggle.IsChecked = false;
                return;
            }
            _webcamDeviceId = first.SymbolicLink;
        }
        _webcamOn = true;
        StartWebcamPreview();
    }

    private void WebcamPickerBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var devices = VideoDevices.ListCameras();
        if (devices.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No cameras found", IsEnabled = false });
        }
        else
        {
            foreach (var device in devices)
            {
                var item = new MenuItem { Header = device.Name };
                item.Click += (_, _) =>
                {
                    _webcamDeviceId = device.SymbolicLink;
                    _webcamOn = true;
                    WebcamToggle.IsChecked = true;
                    StartWebcamPreview(); // re-open on the newly chosen device
                };
                menu.Items.Add(item);
            }
        }
        menu.PlacementTarget = WebcamPickerBtn;
        menu.IsOpen = true;
    }

    /// <summary>Opens the chosen camera and shows its live feed positioned/sized exactly as it'll
    /// appear in the recording. Closes/reopens cleanly if called again (device switched, toggled
    /// off then on, etc.) rather than assuming it's starting from a clean slate.</summary>
    private void StartWebcamPreview()
    {
        StopWebcamPreview();
        if (_webcamDeviceId == null) return;

        try
        {
            _previewCapture = new WebcamCapture(_webcamDeviceId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open camera:\n{ex.Message}", "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Error);
            _webcamOn = false;
            WebcamToggle.IsChecked = false;
            return;
        }

        var region = _selectedRegion ?? App.CaptureEngine.GetCurrentMonitorBounds();
        _previewWindow = new WebcamLivePreview(_previewCapture, region, _webcamSizeFraction, _webcamAnchorX, _webcamAnchorY);
        _previewWindow.PositionChanged += (x, y) =>
        {
            _webcamAnchorX = x;
            _webcamAnchorY = y;
            // Draggable during an active recording too -- push the new spot straight into the
            // live composite instead of only remembering it for the next recording.
            if (App.Recorder.IsRecording) App.Recorder.SetWebcamPosition(x, y);
        };
        _previewWindow.Show();
    }

    /// <summary>Releases the camera and closes the preview -- called when webcam is toggled off,
    /// the toolbar itself closes, and (critically) right before Start actually opens the same
    /// device for real: most webcams only allow one consumer at a time.</summary>
    private void StopWebcamPreview()
    {
        if (_previewWindow != null) { _previewWindow.Close(); _previewWindow = null; }
        if (_previewCapture != null) { _previewCapture.Dispose(); _previewCapture = null; }
    }

    // ---- setup state: settings popup (frame rate + draw-cursor) ----

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var header = new MenuItem { Header = "Frame rate", IsEnabled = false, FontWeight = FontWeights.SemiBold };
        menu.Items.Add(header);
        foreach (int fps in new[] { 8, 12, 15, 24, 30 })
        {
            var item = new MenuItem { Header = $"{fps} fps", IsCheckable = true, IsChecked = _frameRate == fps };
            item.Click += (_, _) => _frameRate = fps;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var cursorItem = new MenuItem { Header = "Show mouse cursor", IsCheckable = true, IsChecked = _drawCursor };
        cursorItem.Click += (_, _) => _drawCursor = cursorItem.IsChecked;
        menu.Items.Add(cursorItem);

        menu.Items.Add(new Separator());
        var sizeHeader = new MenuItem { Header = "Webcam size", IsEnabled = false, FontWeight = FontWeights.SemiBold };
        menu.Items.Add(sizeHeader);
        foreach (var (label, fraction) in new (string, double)[] { ("Small", 0.14), ("Medium", 0.2), ("Large", 0.3) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Math.Abs(_webcamSizeFraction - fraction) < 0.001 };
            item.Click += (_, _) => { _webcamSizeFraction = fraction; _previewWindow?.UpdateSize(fraction); };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var posHint = new MenuItem
        {
            Header = "Webcam position: drag the live preview to place it",
            IsEnabled = false,
            FontStyle = FontStyles.Italic,
        };
        menu.Items.Add(posHint);
        foreach (var (label, x, y) in new (string, double, double)[]
        {
            ("Bottom right", 1, 1), ("Bottom left", 0, 1), ("Top right", 1, 0), ("Top left", 0, 0),
        })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _webcamAnchorX == x && _webcamAnchorY == y };
            item.Click += (_, _) =>
            {
                _webcamAnchorX = x; _webcamAnchorY = y;
                _previewWindow?.SetAnchor(x, y);
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = SettingsBtn;
        menu.IsOpen = true;
    }

    // ---- start / stop / pause ----

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        var region = _selectedRegion ?? App.CaptureEngine.GetCurrentMonitorBounds();
        var options = new RecordingOptions
        {
            FrameRate = _frameRate,
            CaptureCursor = _drawCursor,
            CaptureMicrophone = _micOn,
            MicrophoneDeviceId = _micDeviceId,
            CaptureSystemAudio = _systemAudioOn,
            CaptureWebcam = _webcamOn,
            WebcamDeviceId = _webcamDeviceId,
            SharedWebcamCapture = _previewCapture, // hand the recorder the SAME open capture the
                                                    // live preview is already reading from, rather
                                                    // than opening a second one -- keeps the preview
                                                    // visible (accurately mirroring the recording)
                                                    // the whole time instead of vanishing at Start.
            WebcamAnchorX = _webcamAnchorX,
            WebcamAnchorY = _webcamAnchorY,
            WebcamSizeFraction = _webcamSizeFraction,
        };

        StartBtn.IsEnabled = false;
        try { await CaptureController.StartRecordingAsync(region, options); }
        finally { StartBtn.IsEnabled = true; }
    }

    // Immediate feedback (disabled button, "Stopping…" label) is driven centrally from
    // CaptureController.StopRecordingAsync() via SetStoppingState -- see its own doc comment -- so
    // every trigger (this button, the hotkey, the tray menu) behaves the same way, not just a click
    // reaching this specific handler.
    private async void StopBtn_Click(object sender, RoutedEventArgs e) => await CaptureController.StopRecordingAsync();

    private void PauseResumeBtn_Click(object sender, RoutedEventArgs e) => CaptureController.TogglePauseAsync();

    private void MicMuteToggle_Click(object sender, RoutedEventArgs e) =>
        App.Recorder.SetMicrophoneMuted(MicMuteToggle.IsChecked != true);

    private void SystemAudioMuteToggle_Click(object sender, RoutedEventArgs e) =>
        App.Recorder.SetSystemAudioMuted(SystemAudioMuteToggle.IsChecked != true);

    private void WebcamRecordingToggle_Click(object sender, RoutedEventArgs e)
    {
        bool visible = WebcamRecordingToggle.IsChecked == true;
        App.Recorder.SetWebcamVisible(visible);
        // Keep the on-screen preview's visibility matching what's actually being composited in --
        // otherwise "hidden from the recording" and "still shown on screen" would disagree.
        if (_previewWindow != null) _previewWindow.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Recorder.IsRecording) return; // don't let the only visible stop control disappear mid-recording
        Close();
    }

    // ---- state transitions driven by CaptureController (hotkey- or toolbar-triggered alike) ----

    /// <summary>Called via CaptureController.HideIdleRecordingToolbar when the main window closes.
    /// Releases the camera (so its capture light actually turns off) rather than just hiding the
    /// preview window -- "closing the app" shouldn't leave a device silently in use. Restarted
    /// automatically next time this toolbar reaches Setup state, if webcam is still toggled on.</summary>
    public void HideForIdle()
    {
        StopWebcamPreview();
        Hide();
    }

    /// <summary>Resets to Setup state and shows the toolbar -- used when opening it fresh (Record
    /// Video button, tray menu) or recovering from a failed Start, NOT after a normal Stop (see
    /// <see cref="ResetAndHideAfterStop"/> for that).</summary>
    public void ShowSetupState()
    {
        _clockTimer.Stop();
        RecordingPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        UpdateLayout();
        PositionBottomCenter();
    }

    /// <summary>Called after a recording actually stops. A full reset, not just "back to setup with
    /// the same webcam still going" -- the toolbar itself goes away too (nothing left to control
    /// until the next recording), matching "no menu needed until I click Record again."</summary>
    public void ResetAndHideAfterStop()
    {
        _clockTimer.Stop();
        StopWebcamPreview();
        _webcamOn = false;
        WebcamToggle.IsChecked = false;

        RecordingPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        UpdateLayout();
        PositionBottomCenter();

        Hide();
    }

    public void ShowRecordingState(RecordingOptions options)
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        RecordingPanel.Visibility = Visibility.Visible;

        MicMuteToggle.Visibility = options.CaptureMicrophone ? Visibility.Visible : Visibility.Collapsed;
        SystemAudioMuteToggle.Visibility = options.CaptureSystemAudio ? Visibility.Visible : Visibility.Collapsed;
        WebcamRecordingToggle.Visibility = options.CaptureWebcam ? Visibility.Visible : Visibility.Collapsed;
        MicMuteToggle.IsChecked = true;
        SystemAudioMuteToggle.IsChecked = true;
        WebcamRecordingToggle.IsChecked = true;

        RefreshPauseState();
        TimerText.Text = "00:00:00";
        _clockTimer.Start();

        UpdateLayout();
        PositionBottomCenter();
    }

    public void RefreshPauseState()
    {
        bool paused = App.Recorder.IsPaused;
        PauseResumeBtn.Content = paused ? "▶" : "⏸";
        PauseResumeBtn.ToolTip = paused ? "Resume (Ctrl+Shift+P)" : "Pause (Ctrl+Shift+P)";
        RecStateText.Text = paused ? "PAUSED" : "REC";
        RecDot.Opacity = paused ? 0.4 : 1.0;
    }

    /// <summary>Called by CaptureController itself around the stop/finalize sequence, so a stop
    /// triggered via hotkey or the tray menu -- not just this toolbar's own Stop button -- still
    /// gives immediate feedback instead of leaving the toolbar looking unchanged (and clickable)
    /// for however long webcam compositing takes. Always applies (no "only if currently showing
    /// recording state" guard): this toolbar instance is reused across sessions (see EnsureToolbar),
    /// so the reset-to-"Stop" call after finalize has to actually take effect even though
    /// ResetAndHideAfterStop() -- which runs first -- has already collapsed RecordingPanel by then;
    /// skipping it left the button stuck disabled/"Stopping…" for the NEXT recording too.</summary>
    public void SetStoppingState(bool stopping)
    {
        StopBtn.IsEnabled = !stopping;
        PauseResumeBtn.IsEnabled = !stopping;
        StopBtnText.Text = stopping ? "Stopping…" : "Stop";
    }
}
