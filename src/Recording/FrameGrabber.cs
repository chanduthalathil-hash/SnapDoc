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

    /// <param name="videoPath">File to decode frames from.</param>
    /// <param name="maxWidth">When set, asks the video processor MFT to downscale during decode
    /// rather than decoding at full source resolution -- for a caller like the timeline's filmstrip
    /// that only ever displays a ~64px-wide thumbnail per cell but keeps every grabbed frame alive in
    /// <see cref="SnapDoc.Views.Controls.TrackClip.ThumbCache"/> for as long as the clip exists,
    /// decoding (and retaining) a full 1080p+ frame per cell was the actual memory cost, not a
    /// cosmetic one. Omitted
    /// (or if the size probe below fails) falls back to full source resolution, e.g. for a real
    /// snapshot/frame-export caller that needs it. Best-effort: if the video processor doesn't honor
    /// the requested size, <see cref="Width"/>/<see cref="Height"/> just reflect whatever it actually
    /// produced (re-queried after SetCurrentMediaType), so callers never need to assume the hint was
    /// exact.</param>
    public FrameGrabber(string videoPath, int? maxWidth = null)
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

        if (maxWidth is int mw && mw > 0)
        {
            try
            {
                var native = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                MediaFactory.MFGetAttributeSize((IMFAttributes)native, MediaTypeAttributeKeys.FrameSize, out uint nw, out uint nh).CheckError();
                if (nw > 0 && nh > 0)
                {
                    int targetWidth = Math.Max(2, Math.Min(mw, (int)nw) & ~1);
                    int targetHeight = Math.Max(2, (int)Math.Round(targetWidth * (double)nh / nw) & ~1);
                    MediaFactory.MFSetAttributeSize(desiredAttrs, MediaTypeAttributeKeys.FrameSize, (uint)targetWidth, (uint)targetHeight).CheckError();
                }
            }
            catch { /* couldn't probe native size to request a downscaled decode -- fall back to full-res */ }
        }

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
