using System;
using System.Threading.Tasks;
using System.Windows;

namespace SnapDoc.Recording;

/// <summary>
/// Records a screen region to an MP4. Screen recording is the single largest feature by
/// engineering cost -- encoding, audio sync, performance. It is intentionally a STUB so v1
/// ships without it. Wire it up when the capture+annotate+export core is solid.
/// </summary>
public interface IScreenRecorder
{
    bool IsRecording { get; }
    Task StartAsync(Int32Rect regionPhysicalPixels, string outputMp4Path, RecordingOptions options);
    Task StopAsync();
}

public sealed class RecordingOptions
{
    public int FrameRate { get; set; } = 30;
    public bool CaptureCursor { get; set; } = true;
    public bool CaptureSystemAudio { get; set; } = false;
    public bool CaptureMicrophone { get; set; } = false;
}

/// <summary>
/// Placeholder recorder. Throws with guidance if invoked, so the UI can wire the button now
/// and the feature becomes real without any UI changes.
/// </summary>
public sealed class StubScreenRecorder : IScreenRecorder
{
    public bool IsRecording => false;

    public Task StartAsync(Int32Rect region, string outputMp4Path, RecordingOptions options) =>
        throw new NotImplementedException(
            "Screen recording is not implemented in v1.\n\n" +
            "RECOMMENDED IMPLEMENTATION when you get to it:\n" +
            "  1. Frame capture: Windows.Graphics.Capture (Direct3D11CaptureFramePool) for GPU-fast frames.\n" +
            "  2. Encoding: bundle FFmpeg (ffmpeg.exe) and pipe frames to it, OR use Media Foundation\n" +
            "     (SinkWriter with H.264). FFmpeg is far less code and handles audio muxing for you.\n" +
            "  3. Audio: WASAPI loopback for system audio; the default capture device for mic.\n" +
            "  4. Do NOT hand-roll an encoder -- that path leads to months of pain for no gain.\n\n" +
            "Keep this class's interface; just add a real implementation and register it in App.xaml.cs.");

    public Task StopAsync() => Task.CompletedTask;
}
