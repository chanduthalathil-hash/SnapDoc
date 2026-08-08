using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SnapDoc.Recording;
using Vortice.MediaFoundation;

namespace SnapDoc.Views.Controls;

public enum WaveformTrack { System, Mic }
public enum TrackKind { Video, System, Mic }

/// <summary>
/// One independently editable piece of media on one track. Tracks used to share a single global
/// Trim/Cuts/SplitMarkers region applied identically to all four rows -- this is what replaced that,
/// so a clip can be selected, dragged, trimmed, split, and deleted on its own track without touching
/// the others. <see cref="SourceIn"/>/<see cref="SourceOut"/> are positions within <see cref="SourcePath"/>;
/// <see cref="TimelineStart"/> is where it sits on the shared master timeline all four tracks render
/// against. Mutable (not a record) because drag/trim interactively update it in place every mouse-move.
/// </summary>
public sealed class TrackClip
{
    public required string SourcePath { get; init; }
    public required TrackKind Track { get; init; }
    public TimeSpan SourceDuration { get; init; }
    public TimeSpan SourceIn { get; set; }
    public TimeSpan SourceOut { get; set; }
    public TimeSpan TimelineStart { get; set; }
    public TimeSpan Duration => SourceOut - SourceIn;
    public TimeSpan TimelineEnd => TimelineStart + Duration;

    // Cosmetic caches, invalidated whenever SourceIn/SourceOut change (a pure move doesn't need to
    // regenerate -- same content, new position) -- see InvalidateClipContent.
    internal List<BitmapSource?>? ThumbCache;
    internal (float Min, float Max)[]? WaveCache;

    // Bumped by each RegenerateClipContentAsync call for this clip; a background decode only commits
    // its result if this still matches the generation it started with, so a stale decode (superseded
    // by a second trim/split before the first one's Task.Run finished) can't overwrite a newer one --
    // see RegenerateClipContentAsync.
    internal int ContentGeneration;

    public TrackClip Clone() => new()
    {
        SourcePath = SourcePath, Track = Track, SourceDuration = SourceDuration,
        SourceIn = SourceIn, SourceOut = SourceOut, TimelineStart = TimelineStart,
    };
}

/// <summary>
/// The multi-track timeline: a fixed-layout Video/System Audio/Mic stack (rows disabled when a
/// sidecar wasn't captured for this recording -- see VideoEditorWindow), each holding its own
/// independent list of <see cref="TrackClip"/>s that can be selected, dragged, trimmed, split, and
/// deleted per-track. Nothing here touches any file until VideoEditorWindow hands the clip lists to
/// VideoEditExporter, so undo/redo only needs to snapshot/restore the three lists (see CaptureState).
/// Webcam (if the recording has one) has no track of its own -- it's already permanently part of the
/// Video track's own pixels, composited live during recording (see MfScreenRecorder's class doc).
/// </summary>
public partial class EditorTimeline : UserControl
{
    private const double ClipEdgeGrip = 8; // px either side of a clip's edge that grabs a trim handle instead of a move
    private const double MinClipSeconds = 0.1;
    private const double SnapPixels = 8;

    // Filmstrip cells render at ~64px wide (see ThumbWidth below) -- decoding thumbnails at ~2.5x
    // that instead of full source resolution (was: passing no size hint to FrameGrabber at all, so
    // every cell decoded and retained a full 1080p+ frame) keeps ThumbCache from being the multi-
    // hundred-MB memory sink it used to be, while staying sharp on a HiDPI display.
    private const int ThumbnailDecodeWidth = 160;

    public event Action<TimeSpan>? SeekRequested;
    public event Action? EditStarting; // fired right as a drag/trim begins, before the clip is mutated -- pairs with EditsChanged for undo snapshotting
    public event Action? EditsChanged; // fired after any move/trim/split/delete mutation, for undo snapshotting
    public event Action<bool>? SystemMuteToggled;
    public event Action<bool>? MicMuteToggled;

    public TimeSpan Duration { get; private set; }

    /// <summary>1.0 = "Fit" (today's default: the timeline exactly fills its own width). Above 1
    /// zooms in, making the tracks wider than the viewport and horizontally scrollable.</summary>
    public double ZoomFactor { get; private set; } = 1.0;

    public IReadOnlyCollection<TrackClip> Selection => _selection;

    private readonly Dictionary<TrackKind, List<TrackClip>> _tracks = new()
    {
        [TrackKind.Video] = new(), [TrackKind.System] = new(), [TrackKind.Mic] = new(),
    };
    private readonly HashSet<TrackClip> _selection = new();
    public IReadOnlyList<TrackClip> ClipsOn(TrackKind kind) => _tracks[kind];

    private TimeSpan _playheadPosition;
    private bool _suppressGutterEvents;

    private enum DragMode { None, Move, TrimLeft, TrimRight }
    private DragMode _dragMode = DragMode.None;
    private TrackClip? _dragClip;
    private Point _dragStartMouse;
    private TimeSpan _dragOriginalStart, _dragOriginalIn, _dragOriginalOut;
    private bool _dragMoved;

    public EditorTimeline() => InitializeComponent();

    /// <summary>Seeds one clip per available track, each spanning the whole source. Sidecar tracks
    /// are recorded in lockstep with the main capture (same start, stopped together), so they share
    /// the main video's own duration rather than needing a separate probe per file.</summary>
    public void InitializeTracks(TimeSpan duration, string videoPath, string? systemPath, string? micPath)
    {
        Duration = duration;
        foreach (var list in _tracks.Values) list.Clear();
        _selection.Clear();

        _tracks[TrackKind.Video].Add(NewClip(TrackKind.Video, videoPath, duration));
        if (systemPath != null) _tracks[TrackKind.System].Add(NewClip(TrackKind.System, systemPath, duration));
        if (micPath != null) _tracks[TrackKind.Mic].Add(NewClip(TrackKind.Mic, micPath, duration));

        ApplyZoom();
        foreach (var kind in _tracks.Keys) RegenerateClipContentAsync(_tracks[kind].FirstOrDefault());
    }

    private static TrackClip NewClip(TrackKind kind, string path, TimeSpan duration) => new()
    {
        SourcePath = path, Track = kind, SourceDuration = duration,
        SourceIn = TimeSpan.Zero, SourceOut = duration, TimelineStart = TimeSpan.Zero,
    };

    public void SetTrackAvailability(bool hasSystem, bool hasMic)
    {
        SystemUnavailableText.Visibility = hasSystem ? Visibility.Collapsed : Visibility.Visible;
        MicUnavailableText.Visibility = hasMic ? Visibility.Collapsed : Visibility.Visible;
        SystemMuteToggle.IsEnabled = hasSystem;
        MicMuteToggle.IsEnabled = hasMic;
    }

    /// <summary>Called after a drag-and-drop import gives a track its first clip -- flips the
    /// gutter's "not captured" state to normal without needing a full SetTrackAvailability call
    /// (which would also require the caller to know the OTHER two tracks' current state).</summary>
    private void MarkTrackAvailable(TrackKind kind)
    {
        switch (kind)
        {
            case TrackKind.System: SystemUnavailableText.Visibility = Visibility.Collapsed; SystemMuteToggle.IsEnabled = true; break;
            case TrackKind.Mic: MicUnavailableText.Visibility = Visibility.Collapsed; MicMuteToggle.IsEnabled = true; break;
        }
    }

    public void SetSystemMuted(bool muted) { _suppressGutterEvents = true; SystemMuteToggle.IsChecked = muted; _suppressGutterEvents = false; }
    public void SetMicMuted(bool muted) { _suppressGutterEvents = true; MicMuteToggle.IsChecked = muted; _suppressGutterEvents = false; }

    private void SystemMuteToggle_Changed(object sender, RoutedEventArgs e) { if (!_suppressGutterEvents) SystemMuteToggled?.Invoke(SystemMuteToggle.IsChecked == true); }
    private void MicMuteToggle_Changed(object sender, RoutedEventArgs e) { if (!_suppressGutterEvents) MicMuteToggled?.Invoke(MicMuteToggle.IsChecked == true); }

    public void SetZoom(double factor)
    {
        ZoomFactor = Math.Clamp(factor, 0.25, 4.0);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        double viewport = TrackScrollViewer.ActualWidth > 0 ? TrackScrollViewer.ActualWidth : 800;
        double width = Math.Max(1, viewport * ZoomFactor);
        TrackCanvas.Width = width;
        RulerCanvas.Width = width;
        LayoutAll();
    }

    private void TrackScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    public void SetPlayhead(TimeSpan position)
    {
        _playheadPosition = position;
        if (PixelsPerSecond > 0) Canvas.SetLeft(Playhead, position.TotalSeconds * PixelsPerSecond);
    }

    /// <summary>Deep-copies all four clip lists (selection is ephemeral UI state, not edit history --
    /// it isn't captured). Used by VideoEditorWindow's undo/redo.</summary>
    public Dictionary<TrackKind, List<TrackClip>> CaptureState() =>
        _tracks.ToDictionary(kv => kv.Key, kv => kv.Value.Select(c => c.Clone()).ToList());

    public void RestoreState(Dictionary<TrackKind, List<TrackClip>> state)
    {
        foreach (var kind in _tracks.Keys.ToList())
        {
            _tracks[kind] = state.TryGetValue(kind, out var list) ? list.Select(c => c.Clone()).ToList() : new List<TrackClip>();
        }
        _selection.Clear();
        RecomputeDuration();
        LayoutAll();
        foreach (var kind in _tracks.Keys) foreach (var c in _tracks[kind]) RegenerateClipContentAsync(c);
    }

    private void RecomputeDuration()
    {
        var ends = _tracks.Values.SelectMany(l => l).Select(c => c.TimelineEnd).ToList();
        Duration = ends.Count > 0 ? ends.Max() : TimeSpan.Zero;
    }

    // ---- split / delete ----

    /// <summary>Cuts every selected clip at the playhead into two independent clips. With nothing
    /// selected, falls back to splitting whichever clip on each track currently sits under the
    /// playhead -- keeps today's forgiving one-click-to-blade-everything behavior available without
    /// requiring a selection first.</summary>
    public bool SplitAtPlayhead()
    {
        var targets = _selection.Count > 0
            ? _selection.ToList()
            : _tracks.Values.SelectMany(l => l).Where(c => _playheadPosition > c.TimelineStart && _playheadPosition < c.TimelineEnd).ToList();

        bool any = false;
        foreach (var clip in targets)
        {
            if (_playheadPosition <= clip.TimelineStart || _playheadPosition >= clip.TimelineEnd) continue;
            var splitOffset = _playheadPosition - clip.TimelineStart;
            var second = new TrackClip
            {
                SourcePath = clip.SourcePath, Track = clip.Track, SourceDuration = clip.SourceDuration,
                SourceIn = clip.SourceIn + splitOffset, SourceOut = clip.SourceOut,
                TimelineStart = _playheadPosition,
            };
            clip.SourceOut = clip.SourceIn + splitOffset;
            _tracks[clip.Track].Add(second);
            InvalidateClipContent(clip);
            InvalidateClipContent(second);
            any = true;
        }

        if (any)
        {
            _selection.Clear();
            foreach (var kind in _tracks.Keys) RedrawTrack(kind);
            EditsChanged?.Invoke();
        }
        return any;
    }

    /// <summary>Removes every selected clip. Lift (default) just removes it, leaving a gap; ripple
    /// also shifts every later clip *on that same track* left to close the gap -- tracks ripple
    /// independently of each other, matching the independent-per-track editing model.</summary>
    public bool DeleteSelection(bool ripple)
    {
        if (_selection.Count == 0) return false;

        foreach (var group in _selection.GroupBy(c => c.Track))
        {
            var list = _tracks[group.Key];
            foreach (var clip in group.OrderBy(c => c.TimelineStart))
            {
                var removedStart = clip.TimelineStart;
                var removedDuration = clip.Duration;
                list.Remove(clip);
                if (ripple)
                    foreach (var later in list.Where(c => c.TimelineStart >= removedStart))
                        later.TimelineStart -= removedDuration;
            }
        }

        _selection.Clear();
        RecomputeDuration();
        foreach (var kind in _tracks.Keys) RedrawTrack(kind);
        EditsChanged?.Invoke();
        return true;
    }

    // ---- layout / rendering ----

    private double PixelsPerSecond => Duration.TotalSeconds > 0 && TrackCanvas.Width > 0
        ? TrackCanvas.Width / Duration.TotalSeconds : 0;

    private TimeSpan TimeAt(double x)
    {
        double pps = PixelsPerSecond;
        if (pps <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Max(0, x / pps));
    }

    private void LayoutAll()
    {
        if (TrackCanvas.Width <= 0) return;
        Canvas.SetLeft(Playhead, _playheadPosition.TotalSeconds * PixelsPerSecond);
        foreach (var kind in _tracks.Keys) RedrawTrack(kind);
        DrawRuler();
    }

    private static readonly TrackKind[] AllTracks = { TrackKind.Video, TrackKind.System, TrackKind.Mic };

    private Canvas RowFor(TrackKind kind) => kind switch
    {
        TrackKind.Video => VideoRow, TrackKind.System => SystemRow, _ => MicRow,
    };

    private Brush AccentFor(TrackKind kind) => (Brush)FindResource(kind switch
    {
        TrackKind.System => "GreenAccent", TrackKind.Mic => "BlueAccent", _ => "TextSecondary",
    });

    private void RedrawTrack(TrackKind kind)
    {
        var row = RowFor(kind);
        row.Children.Clear();
        double pps = PixelsPerSecond;
        if (pps <= 0) return;

        foreach (var clip in _tracks[kind])
        {
            double x = clip.TimelineStart.TotalSeconds * pps;
            double w = Math.Max(3, clip.Duration.TotalSeconds * pps);
            bool selected = _selection.Contains(clip);

            var border = new Border
            {
                Width = w, Height = 52,
                Background = new SolidColorBrush(Colors.Black) { Opacity = 0.001 }, // hit-test fill even over empty content
                BorderBrush = selected ? (Brush)FindResource("OrangeAccent") : (Brush)FindResource("CardBorder"),
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true,
            };

            var content = new Canvas { Width = w, Height = 52 };
            border.Child = content;

            if (kind == TrackKind.Video)
            {
                if (clip.ThumbCache is { Count: > 0 } thumbs)
                {
                    double tw = w / thumbs.Count;
                    for (int i = 0; i < thumbs.Count; i++)
                    {
                        if (thumbs[i] is not { } bmp) continue;
                        var img = new Image { Source = bmp, Width = tw, Height = 52, Stretch = Stretch.UniformToFill };
                        Canvas.SetLeft(img, i * tw);
                        content.Children.Add(img);
                    }
                }
            }
            else
            {
                if (clip.WaveCache is { Length: > 0 } peaks)
                {
                    content.Children.Add(BuildWaveformPolygon(peaks, w, AccentFor(kind)));
                }
            }

            Canvas.SetLeft(border, x);
            row.Children.Add(border);
        }
    }

    private static Polygon BuildWaveformPolygon((float Min, float Max)[] peaks, double width, Brush fill)
    {
        const double h = 52, mid = h / 2;
        double colWidth = width / peaks.Length;
        var points = new PointCollection();
        for (int i = 0; i < peaks.Length; i++) points.Add(new Point(i * colWidth, mid - peaks[i].Max * mid));
        for (int i = peaks.Length - 1; i >= 0; i--) points.Add(new Point(i * colWidth, mid - peaks[i].Min * mid));
        return new Polygon { Points = points, Fill = fill, Opacity = 0.75 };
    }

    private void DrawRuler()
    {
        RulerCanvas.Children.Clear();
        if (Duration <= TimeSpan.Zero || RulerCanvas.Width <= 0) return;
        double pps = PixelsPerSecond;
        int labelCount = Math.Max(2, (int)(RulerCanvas.Width / 80));
        double stepSeconds = Duration.TotalSeconds / labelCount;

        for (int i = 0; i <= labelCount; i++)
        {
            var t = TimeSpan.FromSeconds(i * stepSeconds);
            var tb = new TextBlock { Text = FormatTime(t, stepSeconds), Foreground = (Brush)FindResource("TextSecondary"), FontSize = 10, IsHitTestVisible = false };
            Canvas.SetLeft(tb, Math.Max(0, t.TotalSeconds * pps - 14));
            Canvas.SetTop(tb, 2);
            RulerCanvas.Children.Add(tb);
        }
    }

    /// <summary>Whole seconds (m:ss / h:mm:ss) once ruler ticks are at least a second apart. Below
    /// that, adjacent whole-second labels would be identical text (several "0:00" ticks in a row on
    /// a short clip) -- append tenths, or hundredths for a very short clip's finer ticks, so every
    /// label is distinct.</summary>
    private static string FormatTime(TimeSpan t, double stepSeconds)
    {
        string baseFmt = t.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss";
        if (stepSeconds >= 1) return t.ToString(baseFmt);
        return t.ToString(baseFmt + (stepSeconds >= 0.1 ? @"\.f" : @"\.ff"));
    }

    // ---- mouse interaction: select, seek, drag-move, trim, deselect ----

    private TrackKind? RowAt(double y) => y switch
    {
        >= 0 and < 52 => TrackKind.Video,
        >= 56 and < 108 => TrackKind.System,
        >= 112 and < 164 => TrackKind.Mic,
        _ => null,
    };

    // Exclusive end -- matches VideoEditorWindow.ClipCovering's convention, so a click landing exactly
    // on the boundary between two adjacent clips resolves to the same one playback would show there
    // (the later clip), instead of the two methods disagreeing about who owns that exact instant.
    private TrackClip? ClipAt(TrackKind kind, TimeSpan t) =>
        _tracks[kind].FirstOrDefault(c => t >= c.TimelineStart && t < c.TimelineEnd);

    private void RulerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        SeekRequested?.Invoke(TimeAt(e.GetPosition(RulerCanvas).X));

    // ---- drag-and-drop: import an external audio file onto the System or Mic lane ----

    private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".ogg" };
    private static bool IsAudioFile(string path) => AudioExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    private void TrackCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            RowAt(e.GetPosition(TrackCanvas).Y) is TrackKind.System or TrackKind.Mic &&
            e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && files.Any(IsAudioFile))
        {
            e.Effects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    /// <summary>Replaces the dropped-on track's entire clip list with a single clip spanning the
    /// imported file's own full duration -- the same "swap in a different audio source for this
    /// track" a real NLE's drag-and-drop gives you, and simpler than trying to insert it as one clip
    /// among others at a specific drop position.</summary>
    private void TrackCanvas_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var rowKind = RowAt(e.GetPosition(TrackCanvas).Y);
        if (rowKind is not (TrackKind.System or TrackKind.Mic)) return;
        var kind = rowKind.Value;
        var file = files.FirstOrDefault(IsAudioFile);
        if (file == null) return;

        TimeSpan duration;
        try { duration = VideoEditExporter.ProbeAudioDuration(file); }
        catch { return; } // not decodable as audio -- silently ignore rather than crash the drop

        EditStarting?.Invoke();
        _tracks[kind].Clear();
        _tracks[kind].Add(new TrackClip
        {
            SourcePath = file, Track = kind, SourceDuration = duration,
            SourceIn = TimeSpan.Zero, SourceOut = duration, TimelineStart = TimeSpan.Zero,
        });
        _selection.RemoveWhere(c => c.Track == kind);
        MarkTrackAvailable(kind);
        RecomputeDuration();
        RedrawTrack(kind);
        DrawRuler();
        RegenerateClipContentAsync(_tracks[kind][0]);
        EditsChanged?.Invoke();
    }

    private void TrackCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(TrackCanvas);
        var kind = RowAt(pos.Y);
        var t = TimeAt(pos.X);
        double pps = PixelsPerSecond;

        _dragMode = DragMode.None; _dragClip = null; _dragMoved = false; _dragStartMouse = pos;

        var clip = kind != null ? ClipAt(kind.Value, t) : null;
        if (clip == null)
        {
            _selection.Clear();
            foreach (var k in AllTracks) RedrawTrack(k);
            SeekRequested?.Invoke(t);
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl) { if (!_selection.Remove(clip)) _selection.Add(clip); }
        else if (!_selection.Contains(clip)) { _selection.Clear(); _selection.Add(clip); }
        foreach (var k in AllTracks) RedrawTrack(k);

        double clipStartX = clip.TimelineStart.TotalSeconds * pps;
        double clipEndX = clip.TimelineEnd.TotalSeconds * pps;
        _dragMode = Math.Abs(pos.X - clipStartX) <= ClipEdgeGrip ? DragMode.TrimLeft
                  : Math.Abs(pos.X - clipEndX) <= ClipEdgeGrip ? DragMode.TrimRight
                  : DragMode.Move;
        _dragClip = clip;
        _dragOriginalStart = clip.TimelineStart;
        _dragOriginalIn = clip.SourceIn;
        _dragOriginalOut = clip.SourceOut;
        EditStarting?.Invoke();

        SeekRequested?.Invoke(t);
        TrackCanvas.CaptureMouse();
    }

    private void TrackCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None || _dragClip == null || e.LeftButton != MouseButtonState.Pressed) return;
        double pps = PixelsPerSecond;
        if (pps <= 0) return;

        var pos = e.GetPosition(TrackCanvas);
        double dx = pos.X - _dragStartMouse.X;
        if (Math.Abs(dx) > 2) _dragMoved = true;
        double dSeconds = dx / pps;
        var clip = _dragClip;

        switch (_dragMode)
        {
            case DragMode.Move:
            {
                var newStart = _dragOriginalStart + TimeSpan.FromSeconds(dSeconds);
                if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;
                clip.TimelineStart = SnapMoveTime(newStart, clip);
                break;
            }
            case DragMode.TrimLeft:
            {
                var newIn = Clamp(_dragOriginalIn + TimeSpan.FromSeconds(dSeconds), TimeSpan.Zero, clip.SourceOut - TimeSpan.FromSeconds(MinClipSeconds));
                var appliedDelta = newIn - _dragOriginalIn;
                clip.SourceIn = newIn;
                clip.TimelineStart = _dragOriginalStart + appliedDelta;
                break;
            }
            case DragMode.TrimRight:
            {
                clip.SourceOut = Clamp(_dragOriginalOut + TimeSpan.FromSeconds(dSeconds), clip.SourceIn + TimeSpan.FromSeconds(MinClipSeconds), clip.SourceDuration);
                break;
            }
        }
        RedrawTrack(clip.Track);
    }

    private void TrackCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        TrackCanvas.ReleaseMouseCapture();
        if (_dragMode != DragMode.None && _dragClip != null && _dragMoved)
        {
            var clip = _dragClip;
            EnforceNoOverlap(clip);
            if (_dragMode != DragMode.Move) InvalidateClipContent(clip);
            RecomputeDuration();
            RedrawTrack(clip.Track);
            DrawRuler();
            EditsChanged?.Invoke();
        }
        _dragMode = DragMode.None; _dragClip = null;
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan lo, TimeSpan hi) => v < lo ? lo : (v > hi ? hi : v);

    private TimeSpan SnapMoveTime(TimeSpan t, TrackClip moving)
    {
        double pps = PixelsPerSecond;
        double snapSeconds = pps > 0 ? SnapPixels / pps : 0;
        if (snapSeconds <= 0) return t;

        if (Math.Abs((t - _playheadPosition).TotalSeconds) <= snapSeconds) return _playheadPosition;

        foreach (var c in _tracks[moving.Track])
        {
            if (c == moving) continue;
            if (Math.Abs((t - c.TimelineEnd).TotalSeconds) <= snapSeconds) return c.TimelineEnd;
            var movingEndIfHere = t + moving.Duration;
            if (Math.Abs((movingEndIfHere - c.TimelineStart).TotalSeconds) <= snapSeconds)
            {
                var snapped = c.TimelineStart - moving.Duration;
                return snapped < TimeSpan.Zero ? TimeSpan.Zero : snapped;
            }
        }
        return t;
    }

    /// <summary>Single-pass overlap resolution against same-track neighbors: if the just-moved/trimmed
    /// clip now overlaps another clip on its own track, push it to whichever side (before/after that
    /// neighbor) is closer to where it was dropped. Not a full multi-clip solver, but real NLEs don't
    /// silently allow overlapping clips on one track either, so this keeps the model consistent
    /// enough for split/delete/export to reason about "one clip covers this instant" cleanly.</summary>
    private void EnforceNoOverlap(TrackClip moved)
    {
        foreach (var other in _tracks[moved.Track])
        {
            if (other == moved) continue;
            if (moved.TimelineStart < other.TimelineEnd && moved.TimelineEnd > other.TimelineStart)
            {
                var pushRight = other.TimelineEnd;
                var pushLeft = other.TimelineStart - moved.Duration;
                if (pushLeft < TimeSpan.Zero) moved.TimelineStart = pushRight;
                else
                {
                    double distRight = Math.Abs((pushRight - moved.TimelineStart).TotalSeconds);
                    double distLeft = Math.Abs((pushLeft - moved.TimelineStart).TotalSeconds);
                    moved.TimelineStart = distRight <= distLeft ? pushRight : pushLeft;
                }
            }
        }
    }

    // ---- thumbnails / waveforms (per clip, regenerated on trim/split -- a pure move keeps its cache) ----

    /// <summary>Drops every clip's thumbnail/waveform cache and blanks the filmstrips -- called by
    /// VideoEditorWindow right before Export starts. Even with thumbnails now decoded at a small
    /// fixed size (see ThumbnailDecodeWidth) rather than full source resolution, a long recording
    /// with several clips/zoom levels still adds up to real memory that has no reason to sit alive at
    /// the exact moment the exporter is doing its own frame-by-frame decode/encode work. Pairs with
    /// <see cref="RestoreContentCaches"/>, called once export finishes either way.</summary>
    public void ReleaseContentCaches()
    {
        foreach (var list in _tracks.Values)
            foreach (var c in list) { c.ThumbCache = null; c.WaveCache = null; }
        foreach (var kind in _tracks.Keys) RedrawTrack(kind);
    }

    /// <summary>Regenerates whatever <see cref="ReleaseContentCaches"/> dropped, so the timeline isn't
    /// left with blank filmstrips after an export completes/fails/is cancelled.</summary>
    public void RestoreContentCaches()
    {
        foreach (var list in _tracks.Values)
            foreach (var c in list) RegenerateClipContentAsync(c);
    }

    private void InvalidateClipContent(TrackClip clip)
    {
        clip.ThumbCache = null;
        clip.WaveCache = null;
        RegenerateClipContentAsync(clip);
    }

    private async void RegenerateClipContentAsync(TrackClip? clip)
    {
        if (clip == null) return;
        int generation = ++clip.ContentGeneration;
        double pps = PixelsPerSecond;
        double widthPx = pps > 0 ? Math.Max(3, clip.Duration.TotalSeconds * pps) : 200;

        if (clip.Track == TrackKind.Video)
        {
            const int thumbWidth = 64;
            int count = Math.Max(1, (int)(widthPx / thumbWidth));
            var sourceIn = clip.SourceIn; var duration = clip.Duration; var path = clip.SourcePath;
            var images = await Task.Run(() =>
            {
                var result = new List<BitmapSource?>();
                try
                {
                    using var grabber = new FrameGrabber(path, ThumbnailDecodeWidth);
                    for (int i = 0; i < count; i++)
                        result.Add(grabber.GrabAt(sourceIn + TimeSpan.FromSeconds(duration.TotalSeconds * i / count)));
                }
                catch { /* filmstrip is cosmetic -- a failed grab just leaves that clip sparser */ }
                return result;
            });
            // A newer edit (another trim/split on this same clip) may have started and even finished
            // its own regenerate while this one was decoding -- only commit if nothing superseded us,
            // otherwise this stale result would overwrite the correct, newer one.
            if (clip.ContentGeneration != generation) return;
            clip.ThumbCache = images;
        }
        else
        {
            int columns = Math.Max(20, (int)widthPx);
            var sourceIn = clip.SourceIn; var sourceOut = clip.SourceOut; var path = clip.SourcePath;
            var wave = await Task.Run(() => ComputeWaveformPeaks(path, columns, sourceIn, sourceOut));
            if (clip.ContentGeneration != generation) return;
            clip.WaveCache = wave;
        }

        RedrawTrack(clip.Track);
    }

    private static (float Min, float Max)[]? ComputeWaveformPeaks(string path, int columns, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        IMFSourceReader? reader = null;
        try
        {
            MediaFoundationBootstrap.EnsureStarted();
            reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
            var desired = MediaFactory.MFCreateMediaType();
            var desiredAttrs = (IMFAttributes)desired;
            desiredAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            desiredAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, desired);
            if (sourceIn > TimeSpan.Zero) { try { reader.SetCurrentPosition(sourceIn.Ticks); } catch { } }

            var samples = new List<short>();
            while (true)
            {
                var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, 0, out _, out var flags, out long ts);
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                if (sample == null) continue;

                if (ts >= sourceOut.Ticks) { sample.Dispose(); break; }

                using (sample)
                {
                    using var buffer = sample.ConvertToContiguousBuffer();
                    buffer.Lock(out nint ptr, out _, out int length);
                    try
                    {
                        var bytes = new byte[length];
                        Marshal.Copy(ptr, bytes, 0, length);
                        for (int i = 0; i + 1 < bytes.Length; i += 2) samples.Add(BitConverter.ToInt16(bytes, i));
                    }
                    finally { buffer.Unlock(); }
                }
            }
            if (samples.Count == 0) return null;

            var peaks = new (float Min, float Max)[columns];
            int perColumn = Math.Max(1, samples.Count / columns);
            float maxAbsSeen = 1;
            for (int c = 0; c < columns; c++)
            {
                int start = c * perColumn;
                int end = Math.Min(samples.Count, start + perColumn);
                short min = 0, max = 0;
                for (int i = start; i < end; i++) { if (samples[i] < min) min = samples[i]; if (samples[i] > max) max = samples[i]; }
                peaks[c] = (min / 32768f, max / 32768f);
                maxAbsSeen = Math.Max(maxAbsSeen, Math.Max(Math.Abs(peaks[c].Min), Math.Abs(peaks[c].Max)));
            }

            // A genuinely (near-)silent capture -- no app was making sound -- would otherwise draw as
            // a completely flat, invisible line, which reads as "broken" rather than "captured but
            // quiet". Give it a faint baseline so the row visibly has content either way.
            if (maxAbsSeen < 0.02f)
                for (int c = 0; c < columns; c++) peaks[c] = (-0.04f, 0.04f);

            return peaks;
        }
        catch
        {
            return null; // waveform is cosmetic -- a decode failure just leaves that row blank
        }
        finally { reader?.Dispose(); }
    }
}
