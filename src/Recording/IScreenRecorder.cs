using System;
using System.Threading.Tasks;
using System.Windows;

namespace SnapDoc.Recording;

/// <summary>
/// Records a screen region to an MP4. See <see cref="MfScreenRecorder"/> for the real
/// implementation (Media Foundation SinkWriter, H.264 + optional AAC audio).
/// </summary>
public interface IScreenRecorder
{
    bool IsRecording { get; }
    bool IsPaused { get; }

    /// <summary>Recording time so far, excluding any paused duration -- what the toolbar's timer
    /// displays.</summary>
    TimeSpan Elapsed { get; }

    Task StartAsync(Int32Rect regionPhysicalPixels, string outputMp4Path, RecordingOptions options);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();

    /// <summary>No-op if that source wasn't enabled for the current recording.</summary>
    void SetMicrophoneMuted(bool muted);
    void SetSystemAudioMuted(bool muted);

    /// <summary>Shows/hides the webcam picture-in-picture overlay without stopping the camera
    /// itself. No-op if webcam wasn't enabled for the current recording.</summary>
    void SetWebcamVisible(bool visible);

    /// <summary>Moves the webcam picture-in-picture live, mid-recording -- lets the on-screen
    /// preview stay draggable while recording instead of freezing its position at Start. Same
    /// (0..1, top-left-of-placement-area) convention as <see cref="RecordingOptions.WebcamAnchorX"/>.
    /// No-op if webcam wasn't enabled for the current recording.</summary>
    void SetWebcamPosition(double anchorX, double anchorY);
}

public sealed class RecordingOptions
{
    // GDI BitBlt (the same frame-grab CaptureEngine already uses) is a CPU-side per-frame copy,
    // not a GPU capture path -- 12fps is a deliberately modest default that stays reliable across
    // hardware rather than chasing a smooth-video frame rate. Fine for how-to/tutorial recordings;
    // revisit if/when frame capture moves to Windows.Graphics.Capture.
    public int FrameRate { get; set; } = 12;
    public bool CaptureCursor { get; set; } = true;

    public bool CaptureMicrophone { get; set; } = false;
    /// <summary>NAudio MMDevice id, or null/empty for the system default microphone.</summary>
    public string? MicrophoneDeviceId { get; set; }

    public bool CaptureSystemAudio { get; set; } = false;

    public bool CaptureWebcam { get; set; } = false;
    /// <summary>Media Foundation device symbolic link, as returned by <see cref="VideoDevices.ListCameras"/>.</summary>
    public string? WebcamDeviceId { get; set; }

    /// <summary>If provided, the recorder reads frames from this already-open capture instead of
    /// opening its own -- lets the toolbar's live on-screen preview and the actual recording share
    /// one camera handle instead of fighting over exclusive access, and keeps that preview visible
    /// (accurately mirroring what's landing in the file) for the whole recording, not just before
    /// it starts. The recorder does NOT dispose this; whoever created it still owns its lifecycle.</summary>
    public WebcamCapture? SharedWebcamCapture { get; set; }

    /// <summary>Where the picture-in-picture's top-left corner sits within the area it's allowed to
    /// occupy (inset from the recording's edges by a small margin) -- 0 is that area's left/top
    /// edge, 1 is its right/bottom edge. (1,1) is the bottom-right corner, the default. Either set
    /// directly from the toolbar's corner-preset buttons, or freely via its drag-to-place picker.</summary>
    public double WebcamAnchorX { get; set; } = 1.0;
    public double WebcamAnchorY { get; set; } = 1.0;

    /// <summary>Picture-in-picture width as a fraction of the recorded frame's width.</summary>
    public double WebcamSizeFraction { get; set; } = 0.2;
}
