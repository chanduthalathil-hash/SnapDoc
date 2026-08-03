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
/// second thread and muxed in as an AAC track when either is enabled. Video and (main-track) audio
/// both write to the same <see cref="IMFSinkWriter"/>, which is not documented as thread-safe, so
/// every call into it is serialized through <see cref="_writerLock"/>.
///
/// Mic and system audio are ALSO each written to their own small sidecar file next to the main
/// recording (see <see cref="ResolveSidecarPaths"/>) whenever that source is enabled --
/// "&lt;recording&gt;.tracks/mic.m4a", "system.m4a" -- so SnapDoc's video editor can offer
/// independent volume/mute per track after the fact. Each sidecar has exactly one thread ever
/// writing to it (the audio thread), so unlike the shared main writer, neither needs its own lock.
///
/// Webcam has no sidecar of its own: it's composited into the main recording's own pixels live, one
/// screen frame at a time (see <see cref="WriteVideoFrame"/>), at whatever position/size/visibility
/// was current at that instant -- SetWebcamPosition/SetWebcamVisible take effect on the very next
/// frame, which is what lets the on-screen preview stay draggable mid-recording and have that
/// dragging show up in the actual output. The trade-off against the sidecar-track approach this
/// used to take: baking it in live means the position can't be adjusted after the fact in the
/// editor, but it's simpler, faster (no separate encoder thread/file, no post-record compositing
/// pass with its own retry/finalize-timing concerns), and the recording is complete and playable
/// the instant Stop returns -- no "processing" wait. The capture itself is normally supplied by the
/// caller (<see cref="RecordingOptions.SharedWebcamCapture"/>) rather than opened here, so the same
/// feed can also drive a live on-screen preview for the whole recording, not just before it.
/// </summary>
public sealed class MfScreenRecorder : IScreenRecorder
{
    private readonly ICaptureEngine _captureEngine;
    private readonly object _writerLock = new();
    private readonly object _pauseLock = new();

    // Guards LastWebcamAnchorX/Y/SizeFraction/Visible: written from the UI thread (drag handle,
    // mute toggle) and now read on the video thread every frame to composite the webcam live (see
    // WriteVideoFrame) -- unlike most of this class's other cross-thread state, these four are
    // genuinely read-live-during-recording, so a lock (not just "doesn't matter, only read after
    // Stop") is required to avoid a torn read.
    private readonly object _webcamPosLock = new();

    private IMFSinkWriter? _writer;
    private IMFSinkWriter? _micWriter;
    private IMFSinkWriter? _systemWriter;
    private int _micStreamIndex, _systemStreamIndex;
    private Thread? _videoThread;
    private Thread? _audioThread;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private TaskCompletionSource<object?>? _videoStopped;
    private TaskCompletionSource<object?>? _audioStopped;
    private Exception? _fatalError;

    public string? LastMicAudioPath { get; private set; }
    public string? LastSystemAudioPath { get; private set; }
    public double LastWebcamAnchorX { get; private set; } = 1.0;
    public double LastWebcamAnchorY { get; private set; } = 1.0;
    public double LastWebcamSizeFraction { get; private set; } = 0.2;
    public bool LastWebcamVisible { get; private set; } = true;

    private Stopwatch? _clock;
    private TimeSpan _pausedAccumulated;
    private DateTime? _pauseStartedUtc;

    private AudioCaptureMixer? _audioMixer;
    private WebcamCapture? _webcam;
    private bool _ownsWebcam;

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

        var (micPath, systemPath) = ResolveSidecarPaths(outputMp4Path,
            needMic: mixer != null && options.CaptureMicrophone,
            needSystem: mixer != null && options.CaptureSystemAudio);

        IMFSinkWriter? micWriter = null, systemWriter = null;
        int micStream = -1, systemStream = -1;
        try
        {
            if (micPath != null) micWriter = CreateAudioOnlySinkWriter(micPath, mixer!.SampleRate, mixer.Channels, out micStream);
            if (systemPath != null) systemWriter = CreateAudioOnlySinkWriter(systemPath, mixer!.SampleRate, mixer.Channels, out systemStream);
        }
        catch
        {
            // A sidecar failing to set up shouldn't take down the main recording -- drop just that
            // one track rather than throwing, since the user still gets everything else.
            micWriter?.Dispose(); systemWriter?.Dispose();
            micWriter = systemWriter = null;
            micPath = systemPath = null;
        }

        _writer = writer;
        _micWriter = micWriter; _micStreamIndex = micStream;
        _systemWriter = systemWriter; _systemStreamIndex = systemStream;
        LastMicAudioPath = micWriter != null ? micPath : null;
        LastSystemAudioPath = systemWriter != null ? systemPath : null;
        _audioMixer = mixer;
        _webcam = webcam;
        _ownsWebcam = ownsWebcam;
        lock (_webcamPosLock)
        {
            LastWebcamVisible = true;
            LastWebcamAnchorX = Math.Clamp(options.WebcamAnchorX, 0, 1);
            LastWebcamAnchorY = Math.Clamp(options.WebcamAnchorY, 0, 1);
            LastWebcamSizeFraction = Math.Clamp(options.WebcamSizeFraction, 0.08, 0.5);
        }
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
            _audioThread = new Thread(() => RunAudioLoop(writer, audioStream, mixer, micWriter, micStream, systemWriter, systemStream))
            { IsBackground = true, Name = "SnapDoc Recorder (audio)" };
            _audioThread.Start();
        }

        return Task.CompletedTask;
    }

    /// <summary>Where sidecar tracks for this recording live, one folder per recording so a plain
    /// (non-recursive) "*.mp4" scan of the workspace folder -- see WorkspaceService -- never picks
    /// them up as bogus top-level entries. Null for whichever source isn't needed.</summary>
    private static (string? Mic, string? System) ResolveSidecarPaths(
        string outputMp4Path, bool needMic, bool needSystem)
    {
        if (!needMic && !needSystem) return (null, null);

        string dir = Path.GetDirectoryName(outputMp4Path)!;
        string baseName = Path.GetFileNameWithoutExtension(outputMp4Path);
        string tracksDir = Path.Combine(dir, baseName + ".tracks");
        Directory.CreateDirectory(tracksDir);

        return (
            needMic ? Path.Combine(tracksDir, "mic.m4a") : null,
            needSystem ? Path.Combine(tracksDir, "system.m4a") : null);
    }

    public void SetMicrophoneMuted(bool muted) { if (_audioMixer != null) _audioMixer.MicMuted = muted; }
    public void SetSystemAudioMuted(bool muted) { if (_audioMixer != null) _audioMixer.SystemMuted = muted; }

    // Webcam IS baked live into the main recording's pixels now (see class doc) -- these take effect
    // on the very next frame WriteVideoFrame composites, which is what lets the on-screen preview
    // stay draggable/toggleable mid-recording and have that actually show up in the output. Guarded
    // by _webcamPosLock since the video thread now reads these every frame.
    public void SetWebcamVisible(bool visible) { lock (_webcamPosLock) LastWebcamVisible = visible; }

    public void SetWebcamPosition(double anchorX, double anchorY)
    {
        lock (_webcamPosLock)
        {
            LastWebcamAnchorX = Math.Clamp(anchorX, 0, 1);
            LastWebcamAnchorY = Math.Clamp(anchorY, 0, 1);
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

        // Each sidecar writer has exactly one thread that was ever writing to it, and that thread
        // has already confirmed (above) it's done -- no lock needed here, same reasoning as why
        // RunAudioLoop doesn't take one either.
        FinalizeSidecarWriter(ref _micWriter);
        FinalizeSidecarWriter(ref _systemWriter);

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

    private void FinalizeSidecarWriter(ref IMFSinkWriter? writer)
    {
        if (writer == null) return;
        try { writer.Finalize(); } catch (Exception ex) { _fatalError ??= ex; }
        try { writer.Dispose(); } catch { /* already in a failure/shutdown path */ }
        writer = null;
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

    /// <summary>A standalone single-audio-stream sink writer for a mic/system sidecar -- same
    /// PCM-in/AAC-out shape as the audio half of <see cref="CreateSinkWriter"/>, just its own file
    /// instead of a second stream muxed into the main one (SinkWriter can only target one URL).</summary>
    private static IMFSinkWriter CreateAudioOnlySinkWriter(string outputPath, int sampleRate, int channels, out int streamIndex)
    {
        var sinkAttrs = MediaFactory.MFCreateAttributes(2);
        sinkAttrs.Set(SinkWriterAttributeKeys.DisableThrottling, true).CheckError();
        sinkAttrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true).CheckError();
        var writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, sinkAttrs);

        var outType = MediaFactory.MFCreateMediaType();
        var outAttrs = (IMFAttributes)outType;
        outAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(128_000 / 8)).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)16).CheckError();
        streamIndex = writer.AddStream(outType);

        var inType = MediaFactory.MFCreateMediaType();
        var inAttrs = (IMFAttributes)inType;
        inAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)16).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(2 * channels)).CheckError();
        inAttrs.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(sampleRate * 2 * channels)).CheckError();
        writer.SetInputMediaType(streamIndex, inType, null);

        writer.BeginWriting();
        return writer;
    }

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
                CompositeWebcamLive(ptr, stride, width, height);
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

    /// <summary>Blits the webcam's latest decoded frame onto a just-captured screen frame, in place,
    /// before it's ever encoded -- this is what "baked in live" means: by the time WriteVideoFrame's
    /// sample reaches the SinkWriter, the webcam is already part of its pixels, permanently. No-op if
    /// webcam wasn't enabled, is toggled off (<see cref="LastWebcamVisible"/>), or genuinely has no
    /// frame yet (camera still warming up right after Start).</summary>
    private void CompositeWebcamLive(nint screenPtr, int stride, int width, int height)
    {
        if (_webcam == null) return;

        double anchorX, anchorY, sizeFraction; bool visible;
        lock (_webcamPosLock)
        {
            visible = LastWebcamVisible;
            anchorX = LastWebcamAnchorX; anchorY = LastWebcamAnchorY; sizeFraction = LastWebcamSizeFraction;
        }
        if (!visible) return;

        var frame = _webcam.TryGetLatestFrame();
        if (frame is not { } f) return;

        WebcamCompositor.Composite(screenPtr, stride, width, height, f.Bytes, f.Width, f.Height, anchorX, anchorY, sizeFraction);
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
    private void RunAudioLoop(IMFSinkWriter writer, int streamIndex, AudioCaptureMixer mixer,
        IMFSinkWriter? micWriter, int micStreamIndex, IMFSinkWriter? systemWriter, int systemStreamIndex)
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

                byte[] mixedChunk, micChunk, systemChunk;
                try
                {
                    var (mixed, mic, system) = mixer.ReadAllPcm16(neededBytes);
                    mixedChunk = mixed;
                    micChunk = mic ?? Array.Empty<byte>();
                    systemChunk = system ?? Array.Empty<byte>();
                }
                catch { continue; } // a device hiccup shouldn't kill the recording -- retry next tick
                                     // rather than advancing audioTimeWritten without having written
                                     // anything.

                WriteAudioChunk(writer, streamIndex, mixedChunk, bytesPerSecond, audioTimeWritten);
                if (micWriter != null) WriteAudioChunk(micWriter, micStreamIndex, micChunk, bytesPerSecond, audioTimeWritten, needsLock: false);
                if (systemWriter != null) WriteAudioChunk(systemWriter, systemStreamIndex, systemChunk, bytesPerSecond, audioTimeWritten, needsLock: false);
                audioTimeWritten += (long)(mixedChunk.Length / (double)bytesPerSecond * TimeSpan.TicksPerSecond);
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

    // needsLock: true only for the main writer, which the video thread also writes to concurrently
    // -- the mic/system sidecar writers are only ever touched from this one (audio) thread, so
    // locking those too would just be pointless contention against the main writer's lock.
    private void WriteAudioChunk(IMFSinkWriter writer, int streamIndex, byte[] chunk, int bytesPerSecond, long sampleTime, bool needsLock = true)
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
            if (needsLock) lock (_writerLock) writer.WriteSample(streamIndex, sample);
            else writer.WriteSample(streamIndex, sample);
        }
        finally
        {
            buffer.Dispose();
        }
    }
}
