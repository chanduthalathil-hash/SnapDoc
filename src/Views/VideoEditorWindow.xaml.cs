using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SnapDoc.Services;
using SnapDoc.Models;
using SnapDoc.Recording;
using SnapDoc.Views.Controls;
using Path = System.IO.Path;

namespace SnapDoc.Views;

/// <summary>
/// The multi-track video editor: preview, a three-track timeline (see EditorTimeline), per-track
/// audio mixing, master volume, speed, and basic filters. Webcam (if the recording has one) is
/// already permanently part of the video's own pixels -- composited live during recording, see
/// MfScreenRecorder's class doc -- so there's no separate webcam track or overlay to edit here.
/// Nothing here touches any file until Export -- everything is just numbers describing a plan, which
/// VideoEditExporter turns into a real file on a background thread. That non-destructive model is
/// also what makes undo/redo simple: it's just snapshotting/restoring that plan, never the media.
/// </summary>
public partial class VideoEditorWindow : Window
{
    private readonly RecordingClip _clip;
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private TimeSpan _duration;
    private bool _mediaReady;
    private bool _seekDragging;
    private bool _isFullscreen;
    private int _fps = 30; // overwritten once ProbeFps resolves; a reasonable guess for stepping in the meantime
    private CancellationTokenSource? _exportCts;
    private DateTime _lastExportProgressUtc;
    private Stopwatch? _exportStopwatch;
    private bool _exportWatchdogTimedOut;
    private readonly DispatcherTimer _exportWatchdog = new() { Interval = TimeSpan.FromSeconds(5) };

    // Master-timeline playback clock: Preview.Position alone can't drive it once the video track has
    // more than one clip (trim/split/move can make it gapped or non-contiguous) -- see PositionTimer_Tick.
    private TimeSpan _playheadTime;
    private TrackClip? _activeVideoClip;
    private TrackClip? _activeSystemClip;
    private TrackClip? _activeMicClip;
    private DateTime _lastTickUtc;

    // What SystemAudioPreview/MicAudioPreview's own .Source is currently pointed at -- tracked
    // separately from _activeSystemClip/_activeMicClip because a drag-and-drop import (see
    // EditorTimeline.TrackCanvas_Drop) replaces a track's clip with one from a DIFFERENT file, not
    // just a different position in the same file. Only .Position was ever being kept in sync
    // (see EnsureElementSource), so after deleting a track's clip and dropping in a new audio file,
    // playback kept coming from the old file's still-loaded .Source -- the new import was silently
    // never heard.
    private string? _loadedSystemAudioPath;
    private string? _loadedMicAudioPath;

    private double _systemVolume = 1.0, _micVolume = 1.0, _masterVolume = 1.0;
    private bool _systemMuted, _micMuted, _noiseReduction;
    private double _speedFactor = 1.0;
    private double _brightness, _contrast, _saturation;
    private bool _grayscale;
    private double _qualityMultiplier = 1.0;

    private readonly List<EditState> _undoStack = new();
    private readonly List<EditState> _redoStack = new();
    private EditState? _pendingUndoBaseline;
    private bool _suppressState;

    private sealed record EditState(
        Dictionary<TrackKind, List<TrackClip>> Tracks,
        double SystemVolume, double MicVolume, double MasterVolume, bool SystemMuted, bool MicMuted, bool NoiseReduction,
        double SpeedFactor, double Brightness, double Contrast, double Saturation, bool Grayscale);

    public VideoEditorWindow(RecordingClip clip)
    {
        InitializeComponent();
        _clip = clip;
        TitleText.Text = $"Edit — {clip.Title}";

        // See the previous rebuild's note: WindowStartupLocation="CenterScreen" resolves
        // unreliably when this is the first window shown (e.g. opened straight from a hotkey with
        // no other SnapDoc window up), landing off-screen. Center by hand instead.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width);
        Height = Math.Min(Height, wa.Height);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;

        bool hasSystem = clip.SystemAudioPath != null;
        bool hasMic = clip.MicAudioPath != null;
        Timeline.SetTrackAvailability(hasSystem, hasMic);

        // A recording with neither sidecar (made before per-track editing existed) has exactly one
        // real audio track to control -- showing a "System Audio" + disabled "Mic" pair here would
        // misleadingly suggest two independently-adjustable tracks exist. Relabel the one real
        // slider instead of pretending there's a second one.
        if (!hasSystem && !hasMic)
        {
            SystemAudioLabel.Text = "Combined Audio";
            MicAudioRow.Visibility = Visibility.Collapsed;
            NoSeparateAudioText.Text = "This recording has no separate Mic/System tracks (made before per-track editing existed) -- "
                + "the slider above controls its one original audio track.";
            NoSeparateAudioText.Visibility = Visibility.Visible;
        }
        else
        {
            MicVolumeSlider.IsEnabled = hasMic;
            MicMuteCheck.IsEnabled = hasMic;
            NoSeparateAudioText.Visibility = Visibility.Collapsed;
        }

        Timeline.SeekRequested += t => SeekTo(t);
        Timeline.EditStarting += BeginPendingEdit;
        Timeline.EditsChanged += () =>
        {
            CommitPendingEdit();
            SeekSlider.Maximum = Math.Max(0.01, Timeline.Duration.TotalSeconds);
            // Re-resolves the active clip on every track and, via SeekTo's EnsureElementSource calls,
            // reloads a preview MediaElement whose active clip now points at a different file --
            // e.g. a drag-and-drop audio import replacing what was on the System/Mic track. Without
            // this, the old file kept playing until the next unrelated seek/play happened to trigger
            // the same check.
            SeekTo(_playheadTime);
        };
        Timeline.SystemMuteToggled += muted => { SystemMuteCheck.IsChecked = muted; ApplyLiveAudioGains(); };
        Timeline.MicMuteToggled += muted => { MicMuteCheck.IsChecked = muted; ApplyLiveAudioGains(); };
        Timeline.SetSystemMuted(_systemMuted);
        Timeline.SetMicMuted(_micMuted);

        _positionTimer.Tick += PositionTimer_Tick;

        Preview.Source = new Uri(_clip.FilePath);
        Preview.Play();
        Preview.Pause();

        // Fast metadata-only probe (not a decode), but still off the UI thread on principle -- see
        // VideoEditExporter.ProbeFps, the exact fps the export itself will encode at.
        Task.Run(() => { try { return VideoEditExporter.ProbeFps(_clip.FilePath); } catch { return 30; } })
            .ContinueWith(t => _fps = t.Result, TaskScheduler.FromCurrentSynchronizationContext());

        // Hidden audio-only players for the Audio Mix sliders/mute to actually be audible during
        // playback -- Preview's own baked (mixed) audio gets silenced instead once these exist, see
        // ApplyLiveAudioGains. Without these, System/Mic volume and mute only ever affected Export.
        if (hasSystem)
        {
            SystemAudioPreview.Source = new Uri(clip.SystemAudioPath!);
            SystemAudioPreview.Play();
            SystemAudioPreview.Pause();
            _loadedSystemAudioPath = clip.SystemAudioPath;
        }
        if (hasMic)
        {
            MicAudioPreview.Source = new Uri(clip.MicAudioPath!);
            MicAudioPreview.Play();
            MicAudioPreview.Pause();
            _loadedMicAudioPath = clip.MicAudioPath;
        }
        ApplyLiveAudioGains();

        SetActiveTool("Trim");
        RefreshStorageWidget();
        ZoomCombo.SelectedIndex = 0; // after InitializeComponent -- ZoomCombo_SelectionChanged reads Timeline, which isn't assigned until then
    }

    /// <summary>Keeps the Audio Mix panel's sliders/mute (and Master Volume) actually audible during
    /// live playback, not just at Export. When separate mic/system sidecars exist, Preview's own baked
    /// (mixed) audio is muted and the two hidden audio-only players carry all the sound instead; for a
    /// legacy recording with neither sidecar, Preview's own audio IS the only track, so the "Combined
    /// Audio" slider (stored in _systemVolume/_systemMuted -- see the constructor's legacy branch)
    /// drives Preview.Volume directly.</summary>
    private void ApplyLiveAudioGains()
    {
        // XAML sets each slider's initial Value, which fires *ValueChanged (and reaches here) during
        // InitializeComponent() itself -- before _clip is assigned by the rest of the constructor.
        // Same class of bug as the ZoomCombo/RadioButton ones noted elsewhere in this file.
        if (_clip == null) return;
        bool hasSeparateTracks = _clip.SystemAudioPath != null || _clip.MicAudioPath != null;
        if (hasSeparateTracks)
        {
            Preview.Volume = 0;
            SystemAudioPreview.Volume = Math.Clamp((_systemMuted ? 0 : _systemVolume) * _masterVolume, 0, 1);
            MicAudioPreview.Volume = Math.Clamp((_micMuted ? 0 : _micVolume) * _masterVolume, 0, 1);
        }
        else
        {
            Preview.Volume = Math.Clamp((_systemMuted ? 0 : _systemVolume) * _masterVolume, 0, 1);
        }
    }

    // ---- sidebar: mirrors MainWindow's own sidebar, see VideoEditorWindow.xaml's Grid.Column="0" ----

    private void NewCapture_Click(object sender, RoutedEventArgs e) => CaptureController.StartRegionCapture();

    private void SidebarHome_Click(object sender, MouseButtonEventArgs e) { App.Tray?.ShowMain("Home"); Close(); }
    private void SidebarScreenshots_Click(object sender, MouseButtonEventArgs e) { App.Tray?.ShowMain("Screenshots"); Close(); }
    private void SidebarRecordings_Click(object sender, MouseButtonEventArgs e) { App.Tray?.ShowMain("Recordings"); Close(); }

    /// <summary>Same "sum the file sizes already sitting in the in-memory Captures/Recordings
    /// lists" approach MainWindow's home dashboard storage stat uses (see its RefreshHomeDashboard/
    /// SafeFileLength/FormatBytes) -- just also divided against the workspace drive's own real
    /// DriveInfo.TotalSize for the ring/percentage, since MainWindow's version only ever needed the
    /// raw total, not a fraction. Deliberately not an invented quota (e.g. a fixed "10 GB") --
    /// dividing against whatever the drive can actually hold is the only number here that means
    /// anything.</summary>
    private void RefreshStorageWidget()
    {
        try
        {
            long used = App.Workspace.Captures.Sum(c => SafeFileLength(c.FilePath))
                      + App.Workspace.Recordings.Sum(r => SafeFileLength(r.FilePath));
            string root = System.IO.Path.GetPathRoot(App.Settings.WorkspaceFolder) ?? "C:\\";
            long total = new DriveInfo(root).TotalSize;
            double fraction = total > 0 ? Math.Clamp(used / (double)total, 0, 1) : 0;

            StorageUsageText.Text = $"{FormatBytes(used)} / {FormatBytes(total)}";
            StoragePercentText.Text = $"{fraction * 100:0.0}% used";
            StorageRingPath.Data = BuildRingArc(fraction, 16);
        }
        catch { /* storage widget is cosmetic -- a probe failure just leaves it at its default state */ }
    }

    private static long SafeFileLength(string? path)
    {
        try { return string.IsNullOrEmpty(path) ? 0 : new FileInfo(path).Length; }
        catch { return 0; } // file locked/moved/gone -- don't let a stale entry break the total
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private static PathGeometry BuildRingArc(double fraction, double radius)
    {
        fraction = Math.Clamp(fraction, 0.001, 0.999); // a degenerate 0/1 sweep angle draws nothing
        double angle = fraction * 360.0;
        var center = new Point(radius, radius);
        var start = new Point(center.X, center.Y - radius); // 12 o'clock
        double rad = (angle - 90) * Math.PI / 180.0;
        var end = new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void Preview_MediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Preview.NaturalDuration.HasTimeSpan ? Preview.NaturalDuration.TimeSpan : _clip.Duration ?? TimeSpan.Zero;
        _mediaReady = _duration > TimeSpan.Zero;
        ExportBtn.IsEnabled = _mediaReady;
        // Seeds one full-duration clip per available track and generates its thumbnails/waveform --
        // see EditorTimeline.InitializeTracks.
        Timeline.InitializeTracks(_duration, _clip.FilePath, _clip.SystemAudioPath, _clip.MicAudioPath);
        SeekSlider.Maximum = Math.Max(0.01, Timeline.Duration.TotalSeconds);
        UpdateTimeText();
    }

    private void Preview_MediaEnded(object sender, RoutedEventArgs e)
    {
        // The end of Preview's OWN file isn't necessarily the end of the (possibly edited) master
        // timeline -- PositionTimer_Tick already advances past a single clip's end on its own, so
        // MediaEnded here only really fires for the single-clip/no-edits case. Snap back to 0 either way.
        PauseForScrub();
        SeekTo(TimeSpan.Zero);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady) return;
        if (PlayPauseBtn.Content as string == "Play")
        {
            _lastTickUtc = default;
            _activeVideoClip = null; _activeSystemClip = null; _activeMicClip = null;
            _positionTimer.Start();
            PlayPauseBtn.Content = "Pause";
        }
        else
        {
            Preview.Pause();
            SystemAudioPreview.Pause();
            MicAudioPreview.Pause();
            _positionTimer.Stop();
            PlayPauseBtn.Content = "Play";
        }
    }

    private void PauseForScrub()
    {
        Preview.Pause();
        SystemAudioPreview.Pause();
        MicAudioPreview.Pause();
        _positionTimer.Stop();
        PlayPauseBtn.Content = "Play";
    }

    /// <summary>Maps a master-timeline moment onto whichever clip (if any) covers it on the given
    /// track, and seeks that MediaElement to the matching source position. Null clip (a gap, or the
    /// track has nothing) just leaves the element wherever it was -- gaps read as "holds last frame"
    /// for video and "silence" for audio, which is what SyncSecondaryTracksToPlayhead's Pause achieves.</summary>
    private static TrackClip? ClipCovering(EditorTimeline timeline, TrackKind kind, TimeSpan t) =>
        timeline.ClipsOn(kind).FirstOrDefault(c => t >= c.TimelineStart && t < c.TimelineEnd);

    /// <summary>Points an audio-preview MediaElement at whatever file the current clip actually lives
    /// in, reloading .Source only when it's genuinely a different file than what's already loaded --
    /// a drag-and-drop import (see EditorTimeline.TrackCanvas_Drop) replaces a track's clip with one
    /// from a different source file, not just a different position in the same one, and .Source
    /// doesn't follow that on its own the way .Position does every tick. Play()/Pause() immediately
    /// after assigning .Source forces MediaElement to actually load the new file (same trick the
    /// constructor uses), rather than leaving it in "opened but not decoding" limbo.</summary>
    private static void EnsureElementSource(MediaElement element, string sourcePath, ref string? loadedPath)
    {
        if (loadedPath == sourcePath) return;
        element.Source = new Uri(sourcePath);
        element.Play();
        element.Pause();
        loadedPath = sourcePath;
    }

    /// <summary>Explicit scrub/seek (ruler click, clip click, seek slider, frame-step, skip-to-end) --
    /// always re-resolves and re-seeks every track from scratch, unlike the playback tick which only
    /// re-seeks a track when its active clip actually changes (to avoid stuttering mid-clip).</summary>
    private void SeekTo(TimeSpan t)
    {
        var max = Timeline.Duration > TimeSpan.Zero ? Timeline.Duration : _duration;
        t = t < TimeSpan.Zero ? TimeSpan.Zero : t > max ? max : t;
        _playheadTime = t;

        _activeVideoClip = ClipCovering(Timeline, TrackKind.Video, t);
        if (_activeVideoClip != null) Preview.Position = _activeVideoClip.SourceIn + (t - _activeVideoClip.TimelineStart);

        _activeSystemClip = ClipCovering(Timeline, TrackKind.System, t);
        if (_activeSystemClip != null)
        {
            EnsureElementSource(SystemAudioPreview, _activeSystemClip.SourcePath, ref _loadedSystemAudioPath);
            SystemAudioPreview.Position = _activeSystemClip.SourceIn + (t - _activeSystemClip.TimelineStart);
        }

        _activeMicClip = ClipCovering(Timeline, TrackKind.Mic, t);
        if (_activeMicClip != null)
        {
            EnsureElementSource(MicAudioPreview, _activeMicClip.SourcePath, ref _loadedMicAudioPath);
            MicAudioPreview.Position = _activeMicClip.SourceIn + (t - _activeMicClip.TimelineStart);
        }

        Timeline.SetPlayhead(t);
        UpdateTimeText();
    }

    private void PrevFrame_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady) return;
        PauseForScrub();
        SeekTo(_playheadTime - TimeSpan.FromSeconds(1.0 / _fps));
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady) return;
        PauseForScrub();
        SeekTo(_playheadTime + TimeSpan.FromSeconds(1.0 / _fps));
    }

    private void SkipToEnd_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady) return;
        PauseForScrub();
        SeekTo(Timeline.Duration);
    }

    /// <summary>Decodes the current frame straight from the source file via FrameGrabber (the same
    /// helper the timeline's thumbnails and the exporter's webcam compositing already use) rather
    /// than rendering the on-screen MediaElement -- MediaElement's video surface is a
    /// hardware-composited overlay that RenderTargetBitmap frequently can't see through, which was
    /// confirmed here: an earlier RenderTargetBitmap-based version of this produced solid-black
    /// PNGs. Decoding from the file sidesteps that entirely. Saved through the exact same
    /// Capture/WorkspaceService path a screenshot takes, so a video snapshot shows up in the
    /// Screenshots gallery like any other capture.</summary>
    private async void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady) return;
        var position = Preview.Position;
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var grabber = new FrameGrabber(_clip.FilePath);
                return grabber.GrabAt(position);
            });
            if (bmp == null) { StatusText.Text = "Couldn't grab that frame"; return; }

            var capture = new Capture { Image = bmp, Title = $"{_clip.Title} snapshot", CaptureKind = "Video snapshot" };
            App.Workspace.Add(capture);
            try { App.Workspace.SavePng(capture); } catch { /* capture still lives in the workspace list */ }
            StatusText.Text = "Snapshot saved";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save snapshot:\n{ex.Message}", "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        double factor = ZoomCombo.SelectedIndex switch { 1 => 2.0, 2 => 4.0, _ => 1.0 };
        Timeline.SetZoom(factor);
    }

    /// <summary>Hides the sidebar and right panel to give the preview the most room possible --
    /// the transport row (including this same button) stays put so playback control and exiting
    /// fullscreen again are never more than one click away.</summary>
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        _isFullscreen = !_isFullscreen;
        SidebarColumn.Width = new GridLength(_isFullscreen ? 0 : 240);
        RightPanelColumn.Width = new GridLength(_isFullscreen ? 0 : 280);
        TimelineAndToolsPanel.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
        FullscreenBtn.ToolTip = _isFullscreen ? "Exit fullscreen (Esc)" : "Fullscreen preview";
    }

    /// <summary>Drives the master playhead during Play. Preview.Position alone can't do this once the
    /// video track has more than one clip -- trim/split/move can leave it gapped or non-contiguous --
    /// so this walks the master clock forward instead: while the playhead sits inside a video clip,
    /// Preview free-runs and Preview.Position is read back to correct drift; crossing into a different
    /// clip (or into a gap) reseeks/pauses as needed. Secondary tracks (webcam/system/mic) follow the
    /// same active-clip-tracking pattern so they don't get re-seeked every tick mid-clip.</summary>
    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_seekDragging) return;

        var now = DateTime.UtcNow;
        var elapsed = _lastTickUtc == default ? TimeSpan.Zero : now - _lastTickUtc;
        _lastTickUtc = now;

        var covering = ClipCovering(Timeline, TrackKind.Video, _playheadTime);
        if (covering != null)
        {
            if (covering != _activeVideoClip)
            {
                _activeVideoClip = covering;
                Preview.Position = covering.SourceIn + (_playheadTime - covering.TimelineStart);
                Preview.Play();
            }
            _playheadTime = covering.TimelineStart + (Preview.Position - covering.SourceIn);
        }
        else
        {
            if (_activeVideoClip != null) { Preview.Pause(); _activeVideoClip = null; }
            _playheadTime += TimeSpan.FromSeconds(elapsed.TotalSeconds * _speedFactor);
        }

        var lastEnd = Timeline.Duration;
        if (_playheadTime >= lastEnd)
        {
            _playheadTime = lastEnd;
            PauseForScrub();
        }

        SyncSecondaryTrackPlayback(wantPlaying: PlayPauseBtn.Content as string == "Pause");

        SeekSlider.Value = _playheadTime.TotalSeconds;
        Timeline.SetPlayhead(_playheadTime);
        UpdateTimeText();
    }

    /// <summary>Advances the system/mic audio MediaElements to follow the master playhead, re-seeking
    /// each only when its own active clip changes (not every tick) so playback within a clip stays
    /// smooth instead of stuttering from constant micro-seeks.</summary>
    private void SyncSecondaryTrackPlayback(bool wantPlaying)
    {
        AdvanceTrack(ref _activeSystemClip, SystemAudioPreview, ref _loadedSystemAudioPath, TrackKind.System, wantPlaying);
        AdvanceTrack(ref _activeMicClip, MicAudioPreview, ref _loadedMicAudioPath, TrackKind.Mic, wantPlaying);
    }

    private void AdvanceTrack(ref TrackClip? active, MediaElement element, ref string? loadedPath, TrackKind kind, bool wantPlaying)
    {
        var clip = ClipCovering(Timeline, kind, _playheadTime);
        if (clip == active) return;
        active = clip;
        if (clip != null)
        {
            EnsureElementSource(element, clip.SourcePath, ref loadedPath);
            element.Position = clip.SourceIn + (_playheadTime - clip.TimelineStart);
            if (wantPlaying) element.Play();
        }
        else element.Pause();
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _seekDragging = true;

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _seekDragging = false;
        if (!_mediaReady) return;
        SeekTo(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void UpdateTimeText() => TimeText.Text = $"{FormatTime(_playheadTime)} / {FormatTime(Timeline.Duration)}";
    private static string FormatTime(TimeSpan t) => t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---- tool tabs ----

    private void ToolTab_Click(object sender, RoutedEventArgs e)
    {
        string tool = ((ToggleButton)sender).Name.Replace("ToolBtn", "");
        if (tool == "Split")
        {
            // One-shot action (cut the selection, or whatever's under the playhead, right now), not a
            // persistent mode -- so the button doesn't stay visually "on" the way Trim/AudioMix/etc do.
            ((ToggleButton)sender).IsChecked = false;
            BeginPendingEdit();
            bool did = Timeline.SplitAtPlayhead();
            CommitPendingEdit();
            if (!did) StatusText.Text = "Nothing to split there -- select a clip or move the playhead inside one";
            return;
        }
        SetActiveTool(tool);
    }

    private void SetActiveTool(string tool)
    {
        foreach (var btn in new[] { TrimToolBtn, SplitToolBtn, AudioMixToolBtn, VoiceToolBtn, VolumeToolBtn, SpeedToolBtn, FiltersToolBtn })
            btn.IsChecked = false;

        AudioMixPanel.Visibility = tool is "AudioMix" or "Voice" or "Trim" or "Split" ? Visibility.Visible : Visibility.Collapsed;
        VolumePanel.Visibility = tool == "Volume" ? Visibility.Visible : Visibility.Collapsed;
        SpeedPanel.Visibility = tool == "Speed" ? Visibility.Visible : Visibility.Collapsed;
        FiltersPanel.Visibility = tool == "Filters" ? Visibility.Visible : Visibility.Collapsed;

        switch (tool)
        {
            case "Trim": TrimToolBtn.IsChecked = true; break;
            case "Split": SplitToolBtn.IsChecked = true; break;
            case "AudioMix": AudioMixToolBtn.IsChecked = true; break;
            case "Voice": VoiceToolBtn.IsChecked = true; break;
            case "Volume": VolumeToolBtn.IsChecked = true; break;
            case "Speed": SpeedToolBtn.IsChecked = true; break;
            case "Filters": FiltersToolBtn.IsChecked = true; break;
        }
    }

    /// <summary>Lift (default) or ripple (held Shift) delete of whatever's currently selected on the
    /// timeline -- see Window_PreviewKeyDown for the Delete/Shift+Delete key equivalents.</summary>
    private void DeleteTool_Click(object sender, RoutedEventArgs e) => DeleteSelectedClips(ripple: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

    private void DeleteSelectedClips(bool ripple)
    {
        if (Timeline.Selection.Count == 0)
        {
            MessageBox.Show(this, "Click a clip on the timeline to select it first, then Delete.",
                "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        BeginPendingEdit();
        Timeline.DeleteSelection(ripple);
        CommitPendingEdit();
    }

    // ---- top bar: settings gear (export quality) + overflow menu ----

    private void SettingsGear_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = true;
    private void Overflow_Click(object sender, RoutedEventArgs e) => OverflowPopup.IsOpen = true;

    private void QualityOption_Changed(object sender, RoutedEventArgs e)
    {
        _qualityMultiplier = ((RadioButton)sender).Name switch { "QualityHigh" => 0.6, "QualityMedium" => 0.35, _ => 1.0 };
        SettingsPopup.IsOpen = false;
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        if (!File.Exists(_clip.FilePath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_clip.FilePath}\"")); }
        catch { /* Explorer not launchable -- nothing more we can do */ }
    }

    private void RenameRecording_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        string input = Microsoft.VisualBasic.Interaction.InputBox("Recording title:", "Rename", _clip.Title);
        if (string.IsNullOrWhiteSpace(input)) return;
        _clip.Title = input.Trim();
        TitleText.Text = $"Edit — {_clip.Title}";
    }

    private void DeleteRecording_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        var result = MessageBox.Show(this, $"Delete \"{_clip.Title}\"? This can't be undone.", "SnapDoc",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        App.Workspace.DeleteRecording(_clip);
        Close();
    }

    // ---- audio mix ----

    private void SystemVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _systemVolume = e.NewValue / 100.0;
        SystemVolumeText.Text = $"{(int)e.NewValue}%";
        ApplyLiveAudioGains();
    }

    private void MicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _micVolume = e.NewValue / 100.0;
        MicVolumeText.Text = $"{(int)e.NewValue}%";
        ApplyLiveAudioGains();
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _masterVolume = e.NewValue / 100.0;
        MasterVolumeText.Text = $"{(int)e.NewValue}%";
        ApplyLiveAudioGains();
    }

    private void AudioMixOption_Changed(object sender, RoutedEventArgs e)
    {
        BeginPendingEdit();
        _systemMuted = SystemMuteCheck.IsChecked == true;
        _micMuted = MicMuteCheck.IsChecked == true;
        _noiseReduction = NoiseReductionCheck.IsChecked == true;
        ApplyLiveAudioGains();
        Timeline.SetSystemMuted(_systemMuted);
        Timeline.SetMicMuted(_micMuted);
        CommitPendingEdit();
    }

    private void ResetAudioMix_Click(object sender, RoutedEventArgs e)
    {
        BeginPendingEdit();
        SystemVolumeSlider.Value = 100; MicVolumeSlider.Value = 100;
        SystemMuteCheck.IsChecked = false; MicMuteCheck.IsChecked = false; NoiseReductionCheck.IsChecked = false;
        CommitPendingEdit();
    }

    // ---- speed ----

    private void SpeedOption_Changed(object sender, RoutedEventArgs e)
    {
        double factor = ((RadioButton)sender).Name switch
        {
            "Speed05" => 0.5, "Speed075" => 0.75, "Speed125" => 1.25, "Speed15" => 1.5, "Speed2" => 2.0, _ => 1.0,
        };
        BeginPendingEdit();
        _speedFactor = factor;
        Preview.SpeedRatio = factor;
        CommitPendingEdit();
    }

    // ---- filters (baked in at export only -- see class doc for why there's no live preview) ----

    private void FilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _brightness = BrightnessSlider.Value;
        _contrast = ContrastSlider.Value;
        _saturation = SaturationSlider.Value;
    }

    private void FilterOption_Changed(object sender, RoutedEventArgs e)
    {
        BeginPendingEdit();
        _grayscale = GrayscaleCheck.IsChecked == true;
        CommitPendingEdit();
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        BeginPendingEdit();
        BrightnessSlider.Value = 0; ContrastSlider.Value = 0; SaturationSlider.Value = 0;
        GrayscaleCheck.IsChecked = false;
        CommitPendingEdit();
    }

    // ---- undo/redo ----

    private EditState CaptureState() => new(Timeline.CaptureState(), _systemVolume, _micVolume, _masterVolume, _systemMuted, _micMuted, _noiseReduction,
        _speedFactor, _brightness, _contrast, _saturation, _grayscale);

    /// <summary>Call before a mutation that should be undo-able. Pairs with <see cref="CommitPendingEdit"/>;
    /// only the FIRST BeginPendingEdit in a burst (e.g. every tick of a slider drag) actually
    /// captures a baseline, so a whole drag gesture becomes one undo step, not dozens.</summary>
    private void BeginPendingEdit()
    {
        if (_suppressState) return;
        _pendingUndoBaseline ??= CaptureState();
    }

    private void CommitPendingEdit()
    {
        if (_suppressState || _pendingUndoBaseline is not { } baseline) return;
        _pendingUndoBaseline = null;
        _undoStack.Add(baseline);
        _redoStack.Clear();
        UpdateUndoRedoButtons();
    }

    private void RestoreState(EditState s)
    {
        _suppressState = true;
        Timeline.RestoreState(s.Tracks);
        _activeVideoClip = null; _activeSystemClip = null; _activeMicClip = null;

        _systemVolume = s.SystemVolume; _micVolume = s.MicVolume; _masterVolume = s.MasterVolume;
        _systemMuted = s.SystemMuted; _micMuted = s.MicMuted; _noiseReduction = s.NoiseReduction;
        _speedFactor = s.SpeedFactor; _brightness = s.Brightness; _contrast = s.Contrast; _saturation = s.Saturation; _grayscale = s.Grayscale;

        SystemVolumeSlider.Value = _systemVolume * 100; MicVolumeSlider.Value = _micVolume * 100; MasterVolumeSlider.Value = _masterVolume * 100;
        SystemMuteCheck.IsChecked = _systemMuted; MicMuteCheck.IsChecked = _micMuted; NoiseReductionCheck.IsChecked = _noiseReduction;
        BrightnessSlider.Value = _brightness; ContrastSlider.Value = _contrast; SaturationSlider.Value = _saturation; GrayscaleCheck.IsChecked = _grayscale;
        Preview.SpeedRatio = _speedFactor;
        (_speedFactor switch { 0.5 => Speed05, 0.75 => Speed075, 1.25 => Speed125, 1.5 => Speed15, 2.0 => Speed2, _ => Speed1 }).IsChecked = true;
        ApplyLiveAudioGains();
        SeekSlider.Maximum = Math.Max(0.01, Timeline.Duration.TotalSeconds);

        _suppressState = false;
        SeekTo(_playheadTime);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Add(CaptureState());
        var prev = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreState(prev);
        UpdateUndoRedoButtons();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(CaptureState());
        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreState(next);
        UpdateUndoRedoButtons();
    }

    private void UpdateUndoRedoButtons()
    {
        UndoBtn.IsEnabled = _undoStack.Count > 0;
        RedoBtn.IsEnabled = _redoStack.Count > 0;
    }

    private void Slider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => BeginPendingEdit();
    private void Slider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => CommitPendingEdit();

    // ---- export ----

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady) return;

        PauseForScrub();

        string outputPath = BuildOutputPath();
        var plan = new VideoEditPlan
        {
            SourcePath = _clip.FilePath,
            OutputPath = outputPath,
            VideoClips = ToPlanClips(TrackKind.Video),
            SystemClips = ToPlanClips(TrackKind.System),
            MicClips = ToPlanClips(TrackKind.Mic),
            SystemVolume = _systemVolume,
            MicVolume = _micVolume,
            SystemMuted = _systemMuted,
            MicMuted = _micMuted,
            NoiseReduction = _noiseReduction,
            MasterVolume = _masterVolume,
            SpeedFactor = _speedFactor,
            Brightness = _brightness,
            Contrast = _contrast,
            Saturation = _saturation,
            Grayscale = _grayscale,
            QualityMultiplier = _qualityMultiplier,
        };

        ExportBtn.IsEnabled = false;
        CancelExportBtn.Visibility = Visibility.Visible;
        CancelExportBtn.IsEnabled = true;
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        StatusText.Text = "Exporting…";
        _exportCts = new CancellationTokenSource();
        _exportStopwatch = Stopwatch.StartNew();
        _lastExportProgressUtc = DateTime.UtcNow;
        _exportWatchdogTimedOut = false;

        var progress = new Progress<double>(p =>
        {
            _lastExportProgressUtc = DateTime.UtcNow;
            ExportProgress.Value = p * 100;
            // A slow-but-alive export (see VideoEditExporter's per-frame throughput logging) can sit
            // at a fraction of a percent for minutes at a time -- "Exporting… 1%" with an elapsed
            // clock at least shows it's doing something, instead of static text that's indistinguishable
            // from a genuine hang until the watchdog below fires or the user gives up waiting.
            StatusText.Text = p > 0.001
                ? $"Exporting… {p * 100:0.0}% ({_exportStopwatch!.Elapsed:mm\\:ss} elapsed)"
                : $"Exporting… ({_exportStopwatch!.Elapsed:mm\\:ss} elapsed)";
        });

        // Distinguishes "slow but alive" from "actually stuck" the way frame-by-frame progress alone
        // can't -- a WriteSample/ReadSample call that never returns wouldn't trip CancellationToken
        // checks either (MF doesn't observe .NET tokens inside a single native call), so this can't
        // forcibly interrupt one already in flight, but it does mean a genuine stall is reported
        // within seconds of the threshold instead of the user waiting indefinitely on a frozen dialog.
        _exportWatchdog.Tick += ExportWatchdog_Tick;
        _exportWatchdog.Start();

        var masterDuration = Timeline.Duration;

        // Nothing here needs to render while a background export is decoding/encoding its own
        // frames -- see EditorTimeline.ReleaseContentCaches for why keeping the filmstrip/waveform
        // caches alive during export matters, not just after it.
        Timeline.ReleaseContentCaches();
        try
        {
            await Task.Run(() => VideoEditExporter.Export(plan, progress, masterDuration, _exportCts.Token));

            var finalDuration = TimeSpan.FromTicks((long)(masterDuration.Ticks / _speedFactor));
            App.Workspace.AddRecording(outputPath, finalDuration);

            StatusText.Text = "Exported";
            MessageBox.Show(this, $"Saved as a new recording:\n{Path.GetFileName(outputPath)}", "SnapDoc",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _exportWatchdogTimedOut ? "" : "Cancelled";
            if (_exportWatchdogTimedOut)
            {
                CrashLogger.Log("VideoExport",
                    $"Export watchdog timeout for '{_clip.FilePath}' -> '{outputPath}': no progress for 60+ seconds.");
                MessageBox.Show(this,
                    "Export timed out -- no progress for 60 seconds, so it was stopped rather than left waiting " +
                    "indefinitely. This usually means the encoder is stuck; try again, and if it keeps happening " +
                    "please share the SnapDoc log (Overflow menu) so this can be tracked down.",
                    "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best effort cleanup */ }
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            // VideoEditExporter already logged frame index/timestamp/process-memory context for an
            // OOM-flavored failure at the exact moment it happened (see its LogExportFailure) -- this
            // just makes sure whatever reaches here ends up on disk too, and shows something a user
            // can actually act on instead of a raw "HRESULT: [0x8007000E] ..." string.
            CrashLogger.Log("VideoExport", $"Export failed for '{_clip.FilePath}' -> '{outputPath}'.{Environment.NewLine}{ex}");
            string message = IsOutOfMemory(ex)
                ? "Export ran out of memory. Try closing other applications, exporting a shorter section, " +
                  "or lowering the export quality (the gear icon next to Export), then try again."
                : $"Export failed:\n{ex.Message}";
            MessageBox.Show(this, message, "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Error);
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best effort cleanup */ }
        }
        finally
        {
            _exportWatchdog.Stop();
            _exportWatchdog.Tick -= ExportWatchdog_Tick;
            _exportStopwatch = null;
            ExportBtn.IsEnabled = true;
            CancelExportBtn.Visibility = Visibility.Collapsed;
            ExportProgress.Visibility = Visibility.Collapsed;
            _exportCts.Dispose();
            _exportCts = null;
            Timeline.RestoreContentCaches();
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Cancelling…";
        CancelExportBtn.IsEnabled = false;
        _exportCts?.Cancel();
    }

    private void ExportWatchdog_Tick(object? sender, EventArgs e)
    {
        if ((DateTime.UtcNow - _lastExportProgressUtc).TotalSeconds < 60) return;
        _exportWatchdogTimedOut = true;
        _exportCts?.Cancel();
    }

    private static bool IsOutOfMemory(Exception ex) =>
        ex is OutOfMemoryException || ex.HResult == unchecked((int)0x8007000E);

    private List<PlanClip> ToPlanClips(TrackKind kind) =>
        Timeline.ClipsOn(kind).Select(c => new PlanClip(c.SourcePath, c.SourceIn, c.SourceOut, c.TimelineStart)).ToList();

    private string BuildOutputPath()
    {
        string dir = Path.GetDirectoryName(_clip.FilePath)!;
        string baseName = Path.GetFileNameWithoutExtension(_clip.FilePath);
        string path = Path.Combine(dir, $"{baseName} (edited).mp4");
        int n = 2;
        while (File.Exists(path)) path = Path.Combine(dir, $"{baseName} (edited {n++}).mp4");
        return path;
    }

    // ---- window lifecycle ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullscreen) { Fullscreen_Click(sender, e); return; }
        if (!_mediaReady) return;

        switch (e.Key)
        {
            case Key.Delete:
                DeleteSelectedClips(ripple: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true;
                break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.None:
                BeginPendingEdit();
                Timeline.SplitAtPlayhead();
                CommitPendingEdit();
                e.Handled = true;
                break;
            case Key.Left:
                PrevFrame_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Right:
                NextFrame_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exportCts != null)
        {
            var result = MessageBox.Show(this, "An export is still in progress. Cancel it and close?", "SnapDoc",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _exportCts.Cancel();
        }

        _positionTimer.Stop();
        try { Preview.Close(); } catch { /* already tearing down */ }
        try { SystemAudioPreview.Close(); } catch { /* already tearing down */ }
        try { MicAudioPreview.Close(); } catch { /* already tearing down */ }
    }
}
