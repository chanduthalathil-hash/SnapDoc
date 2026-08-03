using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.MediaFoundation;

namespace SnapDoc.Recording;

/// <summary>
/// Grabs single decoded frames from a video file at arbitrary positions, as WPF BitmapSources --
/// used by the editor's timeline to build thumbnail filmstrips (see EditorTimeline). Keeps one
/// SourceReader open and seeks it repeatedly rather than reopening per grab, since a timeline
/// samples many frames from the same file. Not for anything perf-sensitive beyond that -- each
/// grab still seeks and decodes, it's not a cached/streaming reader.
/// </summary>
public sealed class FrameGrabber : IDisposable
{
    private readonly IMFSourceReader _reader;
    public int Width { get; }
    public int Height { get; }

    public FrameGrabber(string videoPath)
    {
        MediaFoundationBootstrap.EnsureStarted();
        // EnableVideoProcessing lets the SourceReader insert a color-conversion MFT -- without it,
        // requesting RGB32 output fails outright for an H.264 source (MF_E_INVALIDMEDIATYPE): the
        // H.264 decoder's own native output is NV12, and only the video processor MFT can get from
        // there to RGB32.
        var readerAttrs = MediaFactory.MFCreateAttributes(1);
        readerAttrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true).CheckError();
        _reader = MediaFactory.MFCreateSourceReaderFromURL(videoPath, readerAttrs);

        var desired = MediaFactory.MFCreateMediaType();
        var desiredAttrs = (IMFAttributes)desired;
        desiredAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        desiredAttrs.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
        _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, desired);

        var actual = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize((IMFAttributes)actual, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h).CheckError();
        Width = (int)w;
        Height = (int)h;
    }

    /// <summary>Null if the position is past the end of the video or nothing could be decoded --
    /// callers just skip that thumbnail rather than treating it as fatal.</summary>
    public BitmapSource? GrabAt(TimeSpan position)
    {
        try { _reader.SetCurrentPosition(position.Ticks); } catch { return null; }

        for (int i = 0; i < 30; i++)
        {
            IMFSample? sample;
            try { sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out _); if ((flags & SourceReaderFlag.EndOfStream) != 0) return null; }
            catch { return null; }
            if (sample == null) continue;

            using (sample)
            {
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out nint ptr, out _, out int length);
                try
                {
                    int stride = Width * 4;
                    var bytes = new byte[stride * Height];
                    Marshal.Copy(ptr, bytes, 0, Math.Min(length, bytes.Length));
                    var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, bytes, stride);
                    bmp.Freeze();
                    return bmp;
                }
                finally { buffer.Unlock(); }
            }
        }
        return null;
    }

    public void Dispose() => _reader.Dispose();
}
