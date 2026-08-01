using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.MediaFoundation;

namespace SnapDoc.Recording;

/// <summary>
/// Grabs frames from a webcam via Media Foundation's SourceReader on a dedicated thread and keeps
/// only the most recently decoded frame available -- the camera's own frame rate doesn't need to
/// (and generally won't) line up with the screen capture's, so "always have the latest frame ready
/// for whenever the video loop wants to composite one" is the right model here, not frame-exact
/// sync. Validated against a real webcam (1280x720, native NV12 -> RGB32 via MF's own converter --
/// the same auto-conversion trick the video encoder setup already relies on) before being wired in.
/// </summary>
public sealed class WebcamCapture : IDisposable
{
    private const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = -4;

    private readonly IMFSourceReader _reader;
    private readonly Thread _thread;
    private volatile bool _stop;

    private readonly object _frameLock = new();
    private byte[]? _latestFrame;
    private readonly int _frameWidth;
    private readonly int _frameHeight;

    public WebcamCapture(string symbolicLink)
    {
        MediaFoundationBootstrap.EnsureStarted();

        var srcAttrs = MediaFactory.MFCreateAttributes(2);
        srcAttrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap).CheckError();
        srcAttrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, symbolicLink).CheckError();
        var source = MediaFactory.MFCreateDeviceSource(srcAttrs);

        var readerAttrs = MediaFactory.MFCreateAttributes(1);
        readerAttrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true).CheckError();
        _reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, readerAttrs);

        // Ask for RGB32 regardless of the camera's native format (commonly NV12 or MJPEG) --
        // EnableVideoProcessing above lets the source reader insert whatever decoder/converter
        // chain is needed, the same way the video encoder's SinkWriter converts RGB32 -> NV12.
        var desired = MediaFactory.MFCreateMediaType();
        var desiredAttrs = (IMFAttributes)desired;
        desiredAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        desiredAttrs.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
        _reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, desired);

        var actualType = _reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
        MediaFactory.MFGetAttributeSize((IMFAttributes)actualType, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h).CheckError();
        _frameWidth = (int)w;
        _frameHeight = (int)h;

        _thread = new Thread(RunCaptureLoop) { IsBackground = true, Name = "SnapDoc Webcam" };
        _thread.Start();
    }

    private void RunCaptureLoop()
    {
        while (!_stop)
        {
            IMFSample? sample;
            try
            {
                sample = _reader.ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out _, out _);
            }
            catch
            {
                break; // device unplugged mid-recording, etc. -- stop quietly, the PiP just disappears
            }
            if (sample == null) continue; // a stream-tick marker, not an actual frame

            using (sample)
            {
                try
                {
                    using var buffer = sample.ConvertToContiguousBuffer();
                    buffer.Lock(out nint ptr, out _, out int currentLength);
                    try
                    {
                        var frame = new byte[currentLength];
                        Marshal.Copy(ptr, frame, 0, currentLength);
                        lock (_frameLock) _latestFrame = frame;
                    }
                    finally { buffer.Unlock(); }
                }
                catch { /* a single bad frame shouldn't kill the webcam feed */ }
            }
        }
    }

    /// <summary>The most recently decoded frame as top-down BGR32 bytes (no real alpha channel --
    /// same layout GDI's screen grabs use), or null if none has arrived yet.</summary>
    public (byte[] Bytes, int Width, int Height)? TryGetLatestFrame()
    {
        lock (_frameLock)
        {
            return _latestFrame == null ? null : (_latestFrame, _frameWidth, _frameHeight);
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread.Join(500); } catch { }
        try { _reader.Dispose(); } catch { }
    }
}
