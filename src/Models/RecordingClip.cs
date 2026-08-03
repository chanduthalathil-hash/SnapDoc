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

    /// <summary>
    /// Sidecar tracks written alongside <see cref="FilePath"/> in a "&lt;basename&gt;.tracks/"
    /// folder next to it -- see MfScreenRecorder's class doc. Null for whichever source wasn't
    /// enabled for this recording, or for one made before this feature existed (no ".tracks"
    /// folder at all). The video editor uses these for independent volume/mute per track; a
    /// recording with neither just edits the same reduced way it always has (trim/cut/replace-or-
    /// remove-audio against the one combined file). Webcam has no sidecar of its own -- it's
    /// composited live into <see cref="FilePath"/>'s own pixels during recording, so there's nothing
    /// separate to edit or mute after the fact.
    /// </summary>
    public string? MicAudioPath { get; init; }
    public string? SystemAudioPath { get; init; }
}
