using System;

namespace SnapDoc.Models;

/// <summary>
/// One saved screen recording. Deliberately thin compared to <see cref="Capture"/> -- there's no
/// annotation/OCR pipeline for video yet, just the file the recorder produced. Named
/// "RecordingClip" rather than "Recording" to avoid colliding with the SnapDoc.Recording namespace
/// (IScreenRecorder et al.).
/// </summary>
public sealed class RecordingClip
{
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>User-editable title, same idea as <see cref="Capture.Title"/> -- shown in the
    /// Recordings gallery and its details panel.</summary>
    public string Title { get; set; } = "Untitled recording";

    /// <summary>Optional user-editable description, shown in the details panel.</summary>
    public string Caption { get; set; } = "";

    public required string FilePath { get; init; }

    /// <summary>
    /// Known for a recording made this session (the recorder timed it); null for one reloaded
    /// from disk on startup, since the video's actual length isn't probed from the file.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    public string DurationText => Duration is { } d ? d.ToString(d.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss") : "";
}
