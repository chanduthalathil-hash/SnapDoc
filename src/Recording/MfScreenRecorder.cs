using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapDoc.CaptureEngine;
using Vortice.MediaFoundation;

namespace SnapDoc.Recording;

/// <summary>
/// Encodes screen frames to H.264/MP4 with Media Foundation's SinkWriter -- Windows' own built-in
/// encoder, so there's no ffmpeg.exe to bundle and no GPL licensing to think about. Frames come
/// from <see cref="ICaptureEngine"/> (the same GDI BitBlt path used for screenshots), grabbed on a
/// dedicated background thread at a fixed cadence and timestamped against a shared logical clock
/// (see <see cref="LogicalTimeTicks"/>) so playback speed holds even across a pause/resume.
///
/// Microphone and system audio (WASAPI via <see cref="AudioCaptureMixer"/>) are captured on a
/// second thread and muxed in as an AAC track when either is enabled. Video and audio both write
/// to the same <see cref="IMFSinkWriter"/>, which is not documented as thread-safe, so every call
/// into it is serialized through <see cref="_writerLock"/>.
///
/// Webcam (<see cref="WebcamCapture"/>) is composited as a picture-in-picture overlay, blitted
/// directly into each frame's pixel buffer before it's handed to the encoder -- plain byte copying,
/// not a WPF Visual/RenderTargetBitmap render, since that machinery expects an STA thread and this
/// recorder's threads don't need to be one for anything else. The capture is normally supplied by
/// the caller (<see cref="RecordingOptions.SharedWebcamCapture"/>) rather than opened here, so the
/// same feed can also drive a live on-screen preview for the whole recording, not just before it.
/// </summary>
public sealed class MfScreenRecorder : IScreenRecorder
{
    private readonly ICaptureEngine _captureEngine;
    private readonly object _writerLock = new();
    private readonly object _pauseLock = new();

    private IMFSinkWriter? _writer;
    private Thread? _videoThread;
    private Thread? _audioThread;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private TaskCompletionSource<object?>? _videoStopped;
    private TaskCompletionSource<object?>? _audioStopped;
    private Exception? _fatalError;

    private Stopwatch? _clock;
    private TimeSpan _pausedAccumulated;
    private DateTime? _pauseStartedUtc;

    private AudioCaptureMixer? _audioMixer;
    private WebcamCapture? _webcam;
    private bool _ownsWebcam;
    private volatile bool _webcamVisible = true;
    private readonly object _webcamPositionLock = new(); // guards the two fields below: written from
                                                           // the UI thread while dragging, read every
                                                           // frame on the video thread -- double can't
                                                           // be marked volatile, so a lock it is.
    private double _webcamAnchorX = 1.0;
    private double _webcamAnchorY = 1.0;
    private double _webcamSizeFraction = 0.2;

    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public TimeSpan Elapsed => _clock == null ? TimeSpan.Zero : TimeSpan.FromTicks(LogicalTimeTicks());

    public MfScreenRecorder(ICaptureEngine captureEngine) => _captureEngine = captureEngine;

    public Task StartAsync(Int32Rect region, string outputMp4Path, RecordingOptions options)
    {
        if (IsRecording) throw new InvalidOperationException("Already recording.");
        if (region.Width < 2 || region.Height < 2)
            throw new ArgumentException("Recording region is too small.", nameof(region));

        MediaFoundationBootstrap.EnsureStarted();

        int width = region.Width - (region.Width % 2); // H.264 needs even dimensions
        int height = region.Height - (region.Height % 2);
        int fps = Math.Max(1, options.FrameRate);

        AudioCaptureMixer? mixer = null;
        if (options.CaptureMicrophone || options.CaptureSystemAudio)
        {
            try
            {
                mixer = new AudioCaptureMixer(options.MicrophoneDeviceId, options.CaptureMicrophone, options.CaptureSystemAudio);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Couldn't start audio capture: {ex.Message}", ex);
            }
        }

        WebcamCapture? webcam = null;
        bool ownsWebcam = false;
        if (options.CaptureWebcam)
        {
            if (options.SharedWebcamCapture != null)
            {
                webcam = options.SharedWebcamCapture; // caller (the toolbar's live preview) owns disposal
            }
            else if (!string.IsNullOrEmpty(options.WebcamDeviceId))
            {
                try
                {
                    webcam = new WebcamCapture(options.WebcamDeviceId);
                    ownsWebcam = true;
                }
                catch (Exception ex)
                {
                    mixer?.Dispose();
                    throw new InvalidOperationException($"Couldn't start webcam: {ex.Message}", ex);
                }
            }
        }

        IMFSinkWriter writer;
        int videoStream, audioStream;
        try
        {
            (writer, videoStream, audioStream) = CreateSinkWriter(outputMp4Path, width, height, fps, mixer);
        }
        catch
        {
            mixer?.Dispose();
            if (ownsWebcam) webcam?.Dispose();
            throw;
        }

        _writer = writer;
        _audioMixer = mixer;
        _webcam = webcam;
        _ownsWebcam = ownsWebcam;
        _webcamVisible = true;
        _webcamAnchorX = Math.Clamp(options.WebcamAnchorX, 0, 1);
        _webcamAnchorY = Math.Clamp(options.WebcamAnchorY, 0, 1);
        _webcamSizeFraction = Math.Clamp(options.WebcamSizeFraction, 0.08, 0.5);
        _clock = Stopwatch.StartNew();
        _pausedAccumulated = TimeSpan.Zero;
        _pauseStartedUtc = null;
        _paused = false;
        _stopRequested = false;
        _fatalError = null;
        _videoStopped = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioStopped = audioStream >= 0 ? new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously) : null;

        var captureRegion = new Int32Rect(region.X, region.Y, width, height);
        bool drawCursor = options.CaptureCursor;

        IsRecording = true;
        mixer?.Start();

        _videoThread = new Thread(() => RunVideoLoop(writer, videoStream, captureRegion, width, height, fps, drawCursor))
        { IsBackground = true, Name = "SnapDoc Recorder (video)" };
        _videoThread.Start();

        if (mixer != null && audioStream >= 0)
        {
            _audioThread = new Thread(() => RunAudioLoop(writer, audioStream, mixer))
            { IsBackground = true, Name = "SnapDoc Recorder (audio)" };
            _audioThread.Start();
        }

        return Task.CompletedTask;
    }

    public void SetMicrophoneMuted(bool muted) { if (_audioMixer != null) _audioMixer.MicMuted = muted; }
    public void SetSystemAudioMuted(bool muted) { if (_audioMixer != null) _audioMixer.SystemMuted = muted; }
    public void SetWebcamVisible(bool visible) => _webcamVisible = visible;

    public void SetWebcamPosition(double anchorX, double anchorY)
    {
        lock (_webcamPositionLock)
        {
            _webcamAnchorX = Math.Clamp(anchorX, 0, 1);
            _webcamAnchorY = Math.Clamp(anchorY, 0, 1);
        }
    }

    public Task PauseAsync()
    {
        if (!IsRecording || _paused) return Task.CompletedTask;
        lock (_pauseLock) { _paused = true; _pauseStartedUtc = DateTime.UtcNow; }
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (!IsRecording || !_paused) return Task.CompletedTask;
        lock (_pauseLock)
        {
            if (_pauseStartedUtc is { } started) _pausedAccumulated += DateTime.UtcNow - started;
            _pauseStartedUtc = null;
            _paused = false;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRecording) return;
        _stopRequested = true;

        if (_videoStopped != null) await _videoStopped.Task;
        if (_audioStopped != null) await _audioStopped.Task;

        // Only safe to Finalize/Dispose once both threads have confirmed they'll touch the writer
        // no more -- doing it from inside either thread risked a race against the other one still
        // mid-WriteSample.
        if (_writer != null)
        {
            lock (_writerLock)
            {
                try { _writer.Finalize(); } catch (Exception ex) { _fatalError ??= ex; }
                try { _writer.Dispose(); } catch { /* already in a failure/shutdown path */ }
            }
            _writer = null;
        }

        _audioMixer?.Stop();
        _audioMixer?.Dispose();
        _audioMixer = null;

        if (_ownsWebcam) _webcam?.Dispose(); // otherwise it's the toolbar's live preview capture -- not ours to dispose
        _webcam = null;
        _ownsWebcam = false;

        IsRecording = false;
        _paused = false;

        var error = _fatalError;
        _fatalError = null;
        if (error != null) throw error;
    }

    /// <summary>The recording's own timeline: real elapsed time minus however long it's spent
    /// paused so far. Both the video and audio loops timestamp samples against this SAME clock,
    /// which is what keeps them in sync -- and because it stops advancing while paused, a
    /// pause/resume doesn't jump the timeline forward or need any special-casing in either loop
    /// beyond "don't write samples while paused".</summary>
    private long LogicalTimeTicks()
    {
        lock (_pauseLock)
        {
            var paused = _pausedAccumulated;
            if (_pauseStartedUtc is { } started) paused += DateTime.UtcNow - started;
            var logical = _clock!.Elapsed - paused;
            return logical.Ticks; // TimeSpan.Ticks are 100ns units -- the same unit MF timestamps use
        }
    }

    private static (IMFSinkWriter writer, int videoStream, int audioStream) CreateSinkWriter(
        string outputPath, int width, int height, int fps, AudioCaptureMixer? mixer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var sinkAttrs = MediaFactory.MFCreateAttributes(2);
        sinkAttrs.Set(SinkWriterAttributeKeys.DisableThrottling, true).CheckError();
        sinkAttrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true).CheckError();

        var writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, sinkAttrs);

        // ---- video stream: RGB32 in, H.264 out (SinkWriter auto-inserts the colour converter) ----
        var outType = MediaFactory.MFCreateMediaType();
        var outAttrs = (IMFAttributes)outType;
        outAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)EstimateVideoBitrate(width, height, fps)).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2 /* MFVideoInterlace_Progressive */).CheckError();
        MediaFactory.MFSetAttributeSize(outAttrs, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(outAttrs, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(outAttrs, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        int videoStream = writer.AddStream(outType);

        var inType = MediaFactory.MFCreateMediaType();
        var inAttrs = (IMFAttributes)inType;
        inAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4)).CheckError();
        MediaFactory.MFSetAttributeSize(inAttrs, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(inAttrs, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(inAttrs, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        writer.SetInputMediaType(videoStream, inType, null);

        // ---- audio stream (optional): 16-bit PCM in, AAC out ----
        int audioStream = -1;
        if (mixer != null)
        {
            var audioOutType = MediaFactory.MFCreateMediaType();
            var audioOutAttrs = (IMFAttributes)audioOutType;
            audioOutAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)mixer.SampleRate).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)mixer.Channels).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(128_000 / 8)).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)16).CheckError();
            audioStream = writer.AddStream(audioOutType);

            var audioInType = MediaFactory.MFCreateMediaType();
            var audioInAttrs = (IMFAttributes)audioInType;
            audioInAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)mixer.SampleRate).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)mixer.Channels).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)16).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(2 * mixer.Channels)).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(mixer.SampleRate * 2 * mixer.Channels)).CheckError();
            writer.SetInputMediaType(audioStream, audioInType, null);
        }

        writer.BeginWriting();
        return (writer, videoStream, audioStream);
    }

    // Screen content (mostly static, text-heavy) compresses far better than natural video, so this
    // stays well under what you'd budget for camera footage at the same resolution/fps.
    private static int EstimateVideoBitrate(int width, int height, int fps) =>
        Math.Clamp((int)(width * height * fps * 0.1), 500_000, 8_000_000);

    private void RunVideoLoop(IMFSinkWriter writer, int streamIndex, Int32Rect region, int width, int height, int fps, bool drawCursor)
    {
        int stride = width * 4;
        int bufferSize = stride * height;
        long frameDurationTicks = TimeSpan.FromSeconds(1.0 / fps).Ticks;
        long nextFrameDue = 0;

        try
        {
            while (!_stopRequested)
            {
                if (_paused) { Thread.Sleep(15); continue; }

                long now = LogicalTimeTicks();
                if (now < nextFrameDue) { Thread.Sleep(1); continue; }

                try
                {
                    var frame = drawCursor ? _captureEngine.CaptureRegionWithCursor(region) : _captureEngine.CaptureRegion(region);
                    WriteVideoFrame(writer, streamIndex, frame, stride, bufferSize, width, height, now, frameDurationTicks);
                }
                catch
                {
                    // One dropped frame (a transient BitBlt failure, say) shouldn't kill an
                    // otherwise multi-minute recording -- just try again next tick.
                }

                nextFrameDue = now + frameDurationTicks;
            }
        }
        catch (Exception ex)
        {
            _fatalError = ex;
        }
        finally
        {
            _videoStopped?.SetResult(null);
        }
    }

    private void WriteVideoFrame(IMFSinkWriter writer, int streamIndex, BitmapSource frame,
        int stride, int bufferSize, int width, int height, long sampleTime, long duration)
    {
        var converted = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        var buffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
        try
        {
            buffer.Lock(out nint ptr, out _, out _);
            try
            {
                converted.CopyPixels(new Int32Rect(0, 0, converted.PixelWidth, converted.PixelHeight), ptr, bufferSize, stride);

                if (_webcam != null && _webcamVisible)
                {
                    var camFrame = _webcam.TryGetLatestFrame();
                    if (camFrame is { } cam)
                    {
                        double anchorX, anchorY;
                        lock (_webcamPositionLock) { anchorX = _webcamAnchorX; anchorY = _webcamAnchorY; }
                        CompositeWebcamOverlay(ptr, stride, width, height, cam.Bytes, cam.Width, cam.Height,
                            anchorX, anchorY, _webcamSizeFraction);
                    }
                }
            }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = bufferSize;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = sampleTime;
            sample.SampleDuration = duration;
            lock (_writerLock) writer.WriteSample(streamIndex, sample);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    // Picture-in-picture: nearest-neighbour scale the webcam frame into a box sized/positioned per
    // the toolbar's settings, and blit it straight into the already-copied screen pixels, with a
    // plain white border. Plain byte-buffer math (not a WPF DrawingVisual/RenderTargetBitmap
    // render) deliberately -- that machinery needs an STA thread, and neither the video nor audio
    // thread here has a reason to be one otherwise.
    private static void CompositeWebcamOverlay(nint screenPtr, int screenStride, int screenWidth, int screenHeight,
        byte[] camBgr32, int camWidth, int camHeight, double anchorX, double anchorY, double sizeFraction)
    {
        if (camWidth <= 0 || camHeight <= 0) return;

        const int BorderPx = 2;
        int pipWidth = Math.Clamp((int)(screenWidth * sizeFraction), 80, screenWidth - 20);
        int pipHeight = Math.Clamp(pipWidth * camHeight / camWidth, 1, Math.Max(1, screenHeight - 20));
        int margin = Math.Max(12, screenWidth / 100);

        // anchorX/Y (0..1) place the box's top-left anywhere within the margin-inset area, same
        // convention as WebcamPositionPicker uses when the user drags it there by hand.
        int minX = margin, maxX = Math.Max(minX, screenWidth - pipWidth - margin);
        int minY = margin, maxY = Math.Max(minY, screenHeight - pipHeight - margin);
        int pipX = minX + (int)((maxX - minX) * Math.Clamp(anchorX, 0, 1));
        int pipY = minY + (int)((maxY - minY) * Math.Clamp(anchorY, 0, 1));

        int camStride = camWidth * 4;
        var rowBuffer = new byte[pipWidth * 4];

        for (int y = 0; y < pipHeight; y++)
        {
            bool horizontalBorder = y < BorderPx || y >= pipHeight - BorderPx;
            int srcY = Math.Min(camHeight - 1, y * camHeight / pipHeight);
            int srcRowStart = srcY * camStride;

            for (int x = 0; x < pipWidth; x++)
            {
                int dstOffset = x * 4;
                if (horizontalBorder || x < BorderPx || x >= pipWidth - BorderPx)
                {
                    rowBuffer[dstOffset] = 255; rowBuffer[dstOffset + 1] = 255;
                    rowBuffer[dstOffset + 2] = 255; rowBuffer[dstOffset + 3] = 255;
                }
                else
                {
                    int srcOffset = srcRowStart + Math.Min(camWidth - 1, x * camWidth / pipWidth) * 4;
                    rowBuffer[dstOffset] = camBgr32[srcOffset];
                    rowBuffer[dstOffset + 1] = camBgr32[srcOffset + 1];
                    rowBuffer[dstOffset + 2] = camBgr32[srcOffset + 2];
                    rowBuffer[dstOffset + 3] = 255;
                }
            }

            nint destRowPtr = screenPtr + (pipY + y) * screenStride + pipX * 4;
            Marshal.Copy(rowBuffer, 0, destRowPtr, rowBuffer.Length);
        }
    }

    /// <summary>
    /// Reads and writes exactly as much audio as real elapsed time says should exist since the
    /// last write -- NOT a fixed byte count every ~100ms sleep. That fixed-size version drifted:
    /// Thread.Sleep(100) is never exactly 100ms (timer-resolution jitter, scheduling under load),
    /// so a cycle that actually took 110ms but only ever drained 100ms worth of bytes leaves 10ms
    /// backlogged in the mixer's buffer every time -- audio falling further and further behind
    /// video the longer the recording runs, which is exactly the "drifts out of sync over time"
    /// symptom this was rewritten to fix. Sizing each read off <see cref="LogicalTimeTicks"/>
    /// (the same clock the video loop timestamps against) keeps the two loops' notions of "how
    /// much time has actually passed" identical, so nothing can accumulate.
    /// </summary>
    private void RunAudioLoop(IMFSinkWriter writer, int streamIndex, AudioCaptureMixer mixer)
    {
        int bytesPerSecond = mixer.SampleRate * mixer.Channels * 2; // 16-bit PCM
        int frameSize = 2 * mixer.Channels; // one sample per channel; keep reads frame-aligned
        long audioTimeWritten = 0; // logical ticks' worth of audio already written

        try
        {
            while (!_stopRequested)
            {
                if (_paused) { Thread.Sleep(15); continue; }

                long now = LogicalTimeTicks();
                long neededTicks = now - audioTimeWritten;
                if (neededTicks < TimeSpan.FromMilliseconds(20).Ticks) { Thread.Sleep(5); continue; }

                int neededBytes = (int)(neededTicks / (double)TimeSpan.TicksPerSecond * bytesPerSecond);
                neededBytes -= neededBytes % frameSize;
                if (neededBytes <= 0) { Thread.Sleep(5); continue; }

                byte[] chunk;
                try { chunk = mixer.ReadMixedPcm16(neededBytes); }
                catch { chunk = Array.Empty<byte>(); continue; } // a device hiccup shouldn't kill the
                                                                   // recording -- retry next tick rather
                                                                   // than advancing audioTimeWritten
                                                                   // without having written anything.

                WriteAudioChunk(writer, streamIndex, chunk, bytesPerSecond, audioTimeWritten);
                audioTimeWritten += (long)(chunk.Length / (double)bytesPerSecond * TimeSpan.TicksPerSecond);
            }
        }
        catch (Exception ex)
        {
            _fatalError ??= ex;
        }
        finally
        {
            _audioStopped?.SetResult(null);
        }
    }

    private void WriteAudioChunk(IMFSinkWriter writer, int streamIndex, byte[] chunk, int bytesPerSecond, long sampleTime)
    {
        long durationTicks = (long)(chunk.Length / (double)bytesPerSecond * 10_000_000L);
        if (durationTicks <= 0) return;

        var buffer = MediaFactory.MFCreateMemoryBuffer(chunk.Length);
        try
        {
            buffer.Lock(out nint ptr, out _, out _);
            Marshal.Copy(chunk, 0, ptr, chunk.Length);
            buffer.Unlock();
            buffer.CurrentLength = chunk.Length;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = sampleTime;
            sample.SampleDuration = durationTicks;
            lock (_writerLock) writer.WriteSample(streamIndex, sample);
        }
        finally
        {
            buffer.Dispose();
        }
    }
}
