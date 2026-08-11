using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SnapDoc.Services;
using Vortice.MediaFoundation;

namespace SnapDoc.Recording;

/// <summary>One piece of a track's timeline: <see cref="SourceIn"/>/<see cref="SourceOut"/> are
/// positions within <see cref="SourcePath"/>; <see cref="TimelineStart"/> is where it sits on the
/// shared master output timeline. Mirrors EditorTimeline's own TrackClip -- kept as a separate,
/// simpler type here since the exporter only needs the four numbers, not the UI-side caches/mutation
/// methods a live-edited clip carries.</summary>
public sealed record PlanClip(string SourcePath, TimeSpan SourceIn, TimeSpan SourceOut, TimeSpan TimelineStart)
{
    public TimeSpan Duration => SourceOut - SourceIn;
    public TimeSpan TimelineEnd => TimelineStart + Duration;
}

/// <summary>
/// One trim/move/split/mix/speed/filter plan for <see cref="VideoEditExporter"/>. Each track (video,
/// system audio, mic) carries its own independent list of <see cref="PlanClip"/>s -- clips can be
/// trimmed, split, reordered, and moved on one track without affecting the others, which is what the
/// timeline's per-track editing needs. An empty System/Mic clip list means "that sidecar wasn't
/// captured for this recording"; when BOTH are empty, the exporter falls back to treating the video
/// clips' own baked-in audio as the system channel (see Export), which is what makes a recording made
/// before per-track sidecars existed still exportable. Webcam (if the recording has one) has no track
/// of its own -- it's already permanently part of the video clips' own pixels, composited live during
/// recording (see MfScreenRecorder's class doc and WebcamCompositor's), so there's nothing separate
/// for this plan to place or for Export to re-composite -- a webcam recording's video clips decode
/// and re-encode through the exact same path as a screen-only one.
/// </summary>
public sealed class VideoEditPlan
{
    public required string SourcePath { get; init; } // the original recording's own file -- used to probe width/height/fps
    public required string OutputPath { get; init; }
    public required List<PlanClip> VideoClips { get; init; }
    public List<PlanClip> SystemClips { get; init; } = new();
    public List<PlanClip> MicClips { get; init; } = new();

    public double SystemVolume { get; init; } = 1.0;
    public double MicVolume { get; init; } = 1.0;
    public bool SystemMuted { get; init; }
    public bool MicMuted { get; init; }
    public bool NoiseReduction { get; init; }
    public double MasterVolume { get; init; } = 1.0;

    public double SpeedFactor { get; init; } = 1.0;
    public double Brightness { get; init; } // -50..50
    public double Contrast { get; init; }   // -50..50
    public double Saturation { get; init; } // -50..50
    public bool Grayscale { get; init; }

    /// <summary>Multiplies the estimated video bitrate -- 1.0 (Original), or lower for a smaller
    /// exported file at the cost of quality (the editor's settings-gear quality picker).</summary>
    public double QualityMultiplier { get; init; } = 1.0;
}

/// <summary>
/// Turns a <see cref="VideoEditPlan"/> into a real file: decodes each track's clips through Media
/// Foundation's SourceReader (unchanged, and never implicated in the problem below -- decoding has
/// shown no evidence of leaking), streams the resulting raw BGRA32 frames straight into a bundled
/// ffmpeg.exe child process for the actual video encode, and hands ffmpeg a temp WAV file for audio.
///
/// This used to encode through a Media Foundation IMFSinkWriter instead (matching what
/// <see cref="MfScreenRecorder"/> uses live). That had a confirmed, precisely-measured native leak:
/// every single frame written retained ~35MB (one frame-buffer's worth) permanently, at a
/// machine-precision-consistent rate, regardless of throttling, hardware-transform, or low-latency
/// settings -- five different targeted fixes to that SinkWriter never touched it, because the
/// retention was happening inside the SinkWriter/encoder's own internal reference-holding, a level
/// none of those attributes actually control. Piping to ffmpeg sidesteps the whole class of bug: an
/// OS pipe has a small, fixed kernel buffer, so writing frames to ffmpeg's stdin blocks the instant
/// ffmpeg falls behind -- real, structural backpressure, not a setting that may or may not be honored
/// by an opaque native component. See FfmpegEncoder for the encoder process itself, and
/// ThirdParty/ffmpeg/NOTICE.txt for exactly which build/license.
///
/// Each track's clips are decoded independently and placed onto the shared master timeline at their
/// own <see cref="PlanClip.TimelineStart"/> -- video seeks once per clip then decodes forward linearly,
/// any span no clip covers renders as black. Audio mixing decodes each track's clips into a
/// full-length silence-padded buffer positioned at their mapped/speed-adjusted byte offsets and sums
/// system+mic -- simpler and more robust than trying to align independently-chunked decode loops
/// sample-for-sample, at the cost of holding the whole track in memory (fine for the recording lengths
/// this is built for).
/// </summary>
public static class VideoEditExporter
{
    /// <summary>The source's own frame rate -- used by the editor for frame-accurate prev/next-frame
    /// stepping. Wraps the same probe <see cref="Export"/> itself uses, so it's the exact fps the
    /// export will actually encode at, not a separately-derived guess.</summary>
    public static int ProbeFps(string sourcePath)
    {
        MediaFoundationBootstrap.EnsureStarted();
        return ProbeVideoFormat(sourcePath).fps;
    }

    /// <summary>Full duration of an arbitrary media file -- used when a user drags an external audio
    /// file onto the System/Mic track (see EditorTimeline's drag-and-drop), since (unlike the
    /// recording's own sidecars) an imported file's length isn't already known.</summary>
    public static TimeSpan ProbeAudioDuration(string path)
    {
        MediaFoundationBootstrap.EnsureStarted();
        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        try
        {
            var duration = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            return TimeSpan.FromTicks(Convert.ToInt64(duration.Value));
        }
        finally { reader.Dispose(); }
    }

    private static bool IsPlainUneditedExport(VideoEditPlan plan)
    {
        if (plan.VideoClips.Count != 1 || plan.SystemClips.Count > 0 || plan.MicClips.Count > 0) return false;
        var clip = plan.VideoClips[0];
        if (clip.SourceIn != TimeSpan.Zero || clip.TimelineStart != TimeSpan.Zero) return false;
        if (Math.Abs(plan.SpeedFactor - 1.0) > 0.0001) return false;
        if (plan.Brightness != 0 || plan.Contrast != 0 || plan.Saturation != 0 || plan.Grayscale) return false;
        if (Math.Abs(plan.QualityMultiplier - 1.0) > 0.0001) return false;
        if (Math.Abs(plan.MasterVolume - 1.0) > 0.0001) return false;

        TimeSpan sourceDuration;
        try { sourceDuration = ProbeAudioDuration(plan.SourcePath); } // generic presentation-duration read -- works for video too, not audio-specific despite the name (its other caller is EditorTimeline's audio-import drag-and-drop)
        catch { return false; } // can't confirm the clip spans the whole file -- don't risk a wrong fast-path decision
        return Math.Abs((clip.SourceOut - sourceDuration).Ticks) < TimeSpan.FromMilliseconds(50).Ticks;
    }

    public static void Export(VideoEditPlan plan, IProgress<double>? progress, TimeSpan masterDuration, CancellationToken ct)
    {
        MediaFoundationBootstrap.EnsureStarted();

        // Logged once per export so a memory-related failure's log entry always has an unambiguous
        // answer to "is this even a 64-bit process" sitting right above it, instead of that having to
        // be re-derived/asserted separately every time this comes up.
        CrashLogger.Log("VideoExport",
            $"Starting export of '{plan.SourcePath}' -> '{plan.OutputPath}'. " +
            $"Process: {(Environment.Is64BitProcess ? "64-bit" : "32-BIT")} (IntPtr.Size={IntPtr.Size}), " +
            $"OS: {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}.");

        // A plan with exactly one video clip spanning the WHOLE original file, no separate audio
        // sidecars, and every adjustment left at its default is "export with no edits" -- the output
        // is exactly the source file. Skipping the entire decode/re-encode pipeline turns that from
        // "however long a full re-encode takes" into "however long a file copy takes". Deliberately
        // excludes a recording that DOES have System/Mic sidecars even at default settings: whether
        // the main file's own baked-in audio is guaranteed byte-identical to freshly re-mixing those
        // sidecars at 100%/100% isn't verified, and a silently-wrong fast path would be worse than a
        // slow-but-correct export.
        if (IsPlainUneditedExport(plan))
        {
            ct.ThrowIfCancellationRequested();
            CrashLogger.Log("VideoExport", "Plan has no edits and no separate audio tracks -- copying the source file directly instead of re-encoding.");
            Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputPath)!);
            File.Copy(plan.SourcePath, plan.OutputPath, overwrite: true);
            progress?.Report(1.0);
            return;
        }

        if (!FfmpegEncoder.IsAvailable)
            throw new InvalidOperationException($"ffmpeg.exe is missing from the install (expected at '{FfmpegEncoder.ExecutablePath}'). Try reinstalling SnapDoc.");

        var (width, height, fps) = ProbeVideoFormat(plan.SourcePath);

        // Neither sidecar exists (a recording made before per-track editing existed) -- fall back to
        // the video clips' own baked-in audio track as "system audio", same as the old single-file model.
        var systemClips = plan.SystemMuted ? new List<PlanClip>()
            : plan.SystemClips.Count > 0 || plan.MicClips.Count > 0 ? plan.SystemClips : plan.VideoClips;
        var micClips = plan.MicMuted ? new List<PlanClip>() : plan.MicClips;

        // Only a liveness check here (does this file even have a decodable audio stream) -- NOT where
        // the output's own sample rate/channels come from. Earlier this derived the output format by
        // probing the first clip's own file, which was fine when that was always the recording's own
        // 48kHz/stereo capture, but a user-imported external file (see EditorTimeline's drag-and-drop)
        // can be mono or an unusual rate. Fixing the output at a known-safe format and letting
        // DecodeClipsToTimeline's forced resample (see its own comment) bring every clip -- original
        // or imported, whatever its native format -- into that same target sidesteps the whole class
        // of "which source's format wins" problem.
        if (systemClips.Count > 0 && ProbeAudioFormat(systemClips[0].SourcePath) == null) systemClips = new();
        if (micClips.Count > 0 && ProbeAudioFormat(micClips[0].SourcePath) == null) micClips = new();

        bool writeAudio = systemClips.Count > 0 || micClips.Count > 0;
        const int audioSampleRate = 48000, audioChannels = 2;

        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputPath)!);

        string? audioWavPath = writeAudio
            ? Path.Combine(Path.GetTempPath(), $"snapdoc_export_audio_{Guid.NewGuid():N}.wav")
            : null;

        try
        {
            if (audioWavPath != null)
                ExportMixedAudioToWav(plan, systemClips, micClips, audioSampleRate, audioChannels, masterDuration.Ticks, audioWavPath, ct);

            string videoEncoder = FfmpegEncoder.DetectBestVideoEncoder();
            int bitrate = (int)(EstimateVideoBitrate(width, height, fps) * plan.QualityMultiplier);
            double effectiveFps = fps * plan.SpeedFactor; // see ExportVideoToFfmpeg's comment on speed

            CrashLogger.Log("VideoExport", $"Encoding via ffmpeg with video encoder '{videoEncoder}', {width}x{height}@{effectiveFps:0.###}fps, bitrate {bitrate}bps, audio: {(audioWavPath != null ? "yes" : "no")}.");

            using var session = FfmpegEncodeSession.Start(width, height, effectiveFps, audioWavPath, plan.OutputPath, bitrate, videoEncoder);
            try
            {
                ExportVideoToFfmpeg(plan, session.VideoInput, width, height, fps, masterDuration, progress, ct);
                progress?.Report(0.95); // all frames handed off -- what's left is ffmpeg finishing/finalizing the file
                session.FinishAndThrowIfFailed(ct);
                progress?.Report(1.0);
            }
            catch
            {
                session.Cancel();
                throw;
            }
        }
        finally
        {
            if (audioWavPath != null) { try { File.Delete(audioWavPath); } catch { /* best effort cleanup */ } }
        }
    }

    // ---- video: per-clip seek-once-then-linear-decode, filters, streamed straight to ffmpeg's stdin ----

    private static void ExportVideoToFfmpeg(VideoEditPlan plan, Stream ffmpegStdin,
        int width, int height, int fps, TimeSpan masterDuration, IProgress<double>? progress, CancellationToken ct)
    {
        int stride = width * 4;
        int bufferSize = stride * height;
        bool needsFilters = plan.Brightness != 0 || plan.Contrast != 0 || plan.Saturation != 0 || plan.Grayscale;
        long frameDurTicks = Math.Max(1, TimeSpan.FromSeconds(1.0 / fps).Ticks);
        long masterTicks = masterDuration.Ticks;

        var clips = plan.VideoClips.Where(c => c.Duration > TimeSpan.Zero).OrderBy(c => c.TimelineStart).ToList();

        // Speed used to be applied by writing every decoded frame with an explicit, compressed/
        // stretched sample timestamp (Media Foundation samples carry their own timing, independent of
        // the container's nominal frame rate). A raw ffmpeg pipe has no per-frame timestamp -- it's
        // strictly constant-rate at whatever -framerate was declared -- so the equivalent here is
        // simpler: declare the input framerate as fps*SpeedFactor (see Export, effectiveFps) and just
        // write every frame in order, undisturbed. Same frame count, same order, different declared
        // rate -- ffmpeg (and every player) shows them faster/slower exactly as before. This is why
        // frames are written here with no per-write timing math at all, unlike the old WriteVideoBytes.
        long outputCursor = 0; // next un-filled output tick (pre-speed timeline space), advances as clips/gaps are written
        int frameIndex = 0;

        // No dedicated diagnostic previously existed for memory *trend* during export -- LogExportFailure
        // (below) only captures a snapshot at the moment of failure, which tells you the peak but not
        // whether it got there by climbing steadily (buffering/leak) or was already high and hit a
        // hard ceiling immediately (address-space limit). Logging all three numbers together every 100
        // frames makes that distinction readable directly off a real run's log. Kept even after moving
        // to ffmpeg specifically so the next run's numbers are directly comparable to the ones that
        // proved the old SinkWriter's leak (~35MB/frame, unbounded) -- this pipeline should stay flat.
        var exportStopwatch = Stopwatch.StartNew();
        void MaybeLogThroughput()
        {
            if (frameIndex == 0 || frameIndex % 100 != 0) return;
            using var proc = Process.GetCurrentProcess();
            double fps2 = frameIndex / Math.Max(0.001, exportStopwatch.Elapsed.TotalSeconds);
            CrashLogger.Log("VideoExportProgress",
                $"frame {frameIndex} | elapsed {exportStopwatch.Elapsed:mm\\:ss} | ~{fps2:0.0} fps | " +
                $"managed heap: {GC.GetTotalMemory(false) / 1024.0 / 1024.0:0} MB | " +
                $"working set: {proc.WorkingSet64 / 1024.0 / 1024.0:0} MB | " +
                $"private bytes: {proc.PrivateMemorySize64 / 1024.0 / 1024.0:0} MB");
        }

        // Still one reused buffer for the whole export (not `new byte[bufferSize]` per frame) -- no
        // longer strictly needed to dodge LOH fragmentation the way it was with the old per-frame
        // native MFCreateMemoryBuffer allocations, but there's no reason to give that up either.
        var frameBuffer = new byte[bufferSize];

        void WriteFrame(byte[] bytes)
        {
            try { ffmpegStdin.Write(bytes, 0, bufferSize); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogExportFailure("writing frame to ffmpeg", frameIndex, outputCursor, ex);
                throw;
            }
        }

        void FillGapWithBlack(long uptoTicks)
        {
            if (outputCursor >= uptoTicks) return;
            var blank = new byte[bufferSize];
            for (; outputCursor < uptoTicks; outputCursor += frameDurTicks)
            {
                ct.ThrowIfCancellationRequested();
                WriteFrame(blank);
                frameIndex++;
            }
        }

        foreach (var clip in clips)
        {
            ct.ThrowIfCancellationRequested();
            FillGapWithBlack(clip.TimelineStart.Ticks);

            var reader = MediaFactory.MFCreateSourceReaderFromURL(clip.SourcePath, VideoProcessingReaderAttrs());
            try
            {
                reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
                var desired = MediaFactory.MFCreateMediaType();
                var desiredAttrs = (IMFAttributes)desired;
                desiredAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                desiredAttrs.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, desired);
                try { reader.SetCurrentPosition(clip.SourceIn.Ticks); } catch { }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    IMFSample? sample;
                    SourceReaderFlag flags;
                    long ts;
                    try
                    {
                        sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out flags, out ts);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogExportFailure($"reading source frame from {clip.SourcePath}", frameIndex, outputCursor, ex);
                        throw;
                    }
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                    if (sample == null) continue;

                    using (sample)
                    {
                        if (ts < clip.SourceIn.Ticks) continue;
                        if (ts >= clip.SourceOut.Ticks) break;

                        long outputTicks = clip.TimelineStart.Ticks + (ts - clip.SourceIn.Ticks);
                        if (outputTicks < outputCursor) continue; // keep the written stream monotonic

                        using var buffer = sample.ConvertToContiguousBuffer();
                        buffer.Lock(out nint ptr, out _, out int length);
                        try { Marshal.Copy(ptr, frameBuffer, 0, Math.Min(length, bufferSize)); }
                        finally { buffer.Unlock(); }

                        if (needsFilters) ApplyFilters(frameBuffer, plan.Brightness, plan.Contrast, plan.Saturation, plan.Grayscale);

                        WriteFrame(frameBuffer);
                        outputCursor = outputTicks + frameDurTicks;
                        frameIndex++;
                        MaybeLogThroughput();

                        if (masterTicks > 0) progress?.Report(Math.Clamp(outputTicks / (double)masterTicks * 0.85, 0, 0.85));
                    }
                }
            }
            finally { reader.Dispose(); }
        }

        FillGapWithBlack(masterTicks);
    }

    /// <summary>Captures exactly what future diagnosis needs when export fails -- where it happened
    /// (a video frame, or the audio mix -- <paramref name="frameIndex"/> negative means "not a
    /// per-frame failure") and what the process's memory looked like at that instant, since by the
    /// time the exception reaches the UI's catch block that context is gone. Written via
    /// <see cref="CrashLogger.Log"/> rather than surfaced to the user directly; Export_Click turns
    /// the exception itself into a plain-language message instead of raw exception text.</summary>
    private static void LogExportFailure(string context, int frameIndex, long outputTicks, Exception ex)
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            double workingSetMb = proc.WorkingSet64 / 1024.0 / 1024.0;
            double privateMb = proc.PrivateMemorySize64 / 1024.0 / 1024.0;
            double managedMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
            string where = frameIndex >= 0
                ? $"writing frame {frameIndex} at {TimeSpan.FromTicks(outputTicks):hh\\:mm\\:ss\\.fff} ({context})"
                : $"during {context}";
            CrashLogger.Log("VideoExport",
                $"Failed {where}. Process memory at failure -- working set: {workingSetMb:0} MB, " +
                $"private bytes: {privateMb:0} MB, managed heap: {managedMb:0} MB.{Environment.NewLine}{ex}");
        }
        catch { /* logging must never mask the real export failure */ }
    }

    // Basic brightness/contrast/saturation/grayscale, applied per-pixel to a BGRA32 buffer -- not a
    // full color-grading pipeline, just enough to be genuinely useful for a quick cleanup pass.
    private static void ApplyFilters(byte[] bgra, double brightness, double contrast, double saturation, bool grayscale)
    {
        double contrastFactor = 1.0 + contrast / 50.0;
        double satFactor = grayscale ? 0.0 : 1.0 + saturation / 50.0;

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            b += brightness; g += brightness; r += brightness;
            b = (b - 128) * contrastFactor + 128; g = (g - 128) * contrastFactor + 128; r = (r - 128) * contrastFactor + 128;
            double luma = 0.114 * b + 0.587 * g + 0.299 * r;
            b = luma + (b - luma) * satFactor; g = luma + (g - luma) * satFactor; r = luma + (r - luma) * satFactor;
            bgra[i] = (byte)Math.Clamp(b, 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(g, 0, 255);
            bgra[i + 2] = (byte)Math.Clamp(r, 0, 255);
        }
    }

    // ---- audio: mix mic + system, each track's clips decoded and placed independently, written to a temp WAV for ffmpeg to mux ----

    private static void ExportMixedAudioToWav(VideoEditPlan plan, List<PlanClip> systemClips, List<PlanClip> micClips,
        int sampleRate, int channels, long masterDurationTicks, string wavPath, CancellationToken ct)
    {
        int bytesPerSecond = sampleRate * channels * 2;
        long finalDurationTicks = (long)(masterDurationTicks / plan.SpeedFactor);
        int totalBytes = (int)(finalDurationTicks / (double)TimeSpan.TicksPerSecond * bytesPerSecond);
        totalBytes -= totalBytes % (2 * channels);
        if (totalBytes <= 0) return;

        try
        {
            byte[]? systemBuf = systemClips.Count > 0
                ? DecodeClipsToTimeline(systemClips, plan.SpeedFactor, plan.SystemVolume, totalBytes, bytesPerSecond, sampleRate, channels, ct) : null;
            byte[]? micBuf = micClips.Count > 0
                ? DecodeClipsToTimeline(micClips, plan.SpeedFactor, plan.MicVolume, totalBytes, bytesPerSecond, sampleRate, channels, ct) : null;
            if (micBuf != null && plan.NoiseReduction) ApplyNoiseGate(micBuf);

            // Mix into whichever buffer already exists instead of allocating a third totalBytes-sized
            // array just to hold the sum -- see AudioCaptureMixer.MixPcm16Into.
            byte[] mixed;
            if (systemBuf != null && micBuf != null) { AudioCaptureMixer.MixPcm16Into(systemBuf, micBuf); mixed = systemBuf; }
            else mixed = systemBuf ?? micBuf ?? Array.Empty<byte>();

            if (Math.Abs(plan.MasterVolume - 1.0) > 0.001) ApplyGain(mixed, plan.MasterVolume);

            ct.ThrowIfCancellationRequested();
            WritePcm16WavFile(wavPath, mixed, sampleRate, channels);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogExportFailure($"audio mix (system clips: {systemClips.Count}, mic clips: {micClips.Count}, " +
                $"track length: {totalBytes / 1024.0 / 1024.0:0.0} MB)", -1, 0, ex);
            throw;
        }
    }

    /// <summary>Minimal 44-byte-header PCM16 WAV writer -- ffmpeg reads this as the export's second
    /// input (alongside the raw video pipe) and does the actual AAC encode + muxing itself.</summary>
    private static void WritePcm16WavFile(string path, byte[] pcm, int sampleRate, int channels)
    {
        int byteRate = sampleRate * channels * 2;
        short blockAlign = (short)(channels * 2);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // PCM fmt chunk size
        bw.Write((short)1); // PCM format tag
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)16); // bits per sample
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm);
    }

    /// <summary>Decodes one track's clips into a zero-filled (silence) buffer sized to the FINAL
    /// output length, seeking once per clip and placing each decoded sample at its mapped/speed-
    /// adjusted byte offset. Building each track's full timeline independently like this -- rather
    /// than trying to keep two live decode loops chunk-aligned -- is what lets system and mic (which
    /// may have entirely different clip arrangements after independent editing) still sum together
    /// correctly: MixPcm16Into just adds two same-length buffers.</summary>
    private static byte[] DecodeClipsToTimeline(List<PlanClip> clips, double speedFactor, double volume,
        int totalBytes, int bytesPerSecond, int sampleRate, int channels, CancellationToken ct)
    {
        var output = new byte[totalBytes];

        foreach (var clip in clips.Where(c => c.Duration > TimeSpan.Zero).OrderBy(c => c.TimelineStart))
        {
            IMFSourceReader? reader = null;
            try
            {
                reader = MediaFactory.MFCreateSourceReaderFromURL(clip.SourcePath, null);
                reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);

                // Force the SAME sample rate/channels the output is being mixed at (not just "give me
                // PCM") so Media Foundation inserts its own resampler MFT when a clip's native format
                // doesn't already match -- needed once a user can drop in an arbitrary external audio
                // file (see EditorTimeline's drag-and-drop), which won't generally share the
                // recording's own 48kHz/stereo capture format. Falls back to an unforced request if a
                // source can't be resampled to the target format for some reason.
                var desired = MediaFactory.MFCreateMediaType();
                var desiredAttrs = (IMFAttributes)desired;
                desiredAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                desiredAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
                desiredAttrs.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate).CheckError();
                desiredAttrs.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels).CheckError();
                try { reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, desired); }
                catch
                {
                    var fallback = MediaFactory.MFCreateMediaType();
                    var fallbackAttrs = (IMFAttributes)fallback;
                    fallbackAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                    fallbackAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
                    reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, fallback);
                }
                try { reader.SetCurrentPosition(clip.SourceIn.Ticks); } catch { }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, 0, out _, out var flags, out long ts);
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                    if (sample == null) continue;

                    using (sample)
                    {
                        if (ts < clip.SourceIn.Ticks) continue;
                        if (ts >= clip.SourceOut.Ticks) break;

                        using var buffer = sample.ConvertToContiguousBuffer();
                        buffer.Lock(out nint ptr, out _, out int length);
                        byte[] pcm;
                        try { pcm = new byte[length]; Marshal.Copy(ptr, pcm, 0, length); }
                        finally { buffer.Unlock(); }

                        if (Math.Abs(volume - 1.0) > 0.001) ApplyGain(pcm, volume);
                        if (Math.Abs(speedFactor - 1.0) > 0.001) pcm = ResampleForSpeed(pcm, channels, speedFactor);

                        long outputTicks = clip.TimelineStart.Ticks + (ts - clip.SourceIn.Ticks);
                        long destTick = (long)(outputTicks / speedFactor);
                        int destByteOffset = (int)(destTick / (double)TimeSpan.TicksPerSecond * bytesPerSecond);
                        destByteOffset -= destByteOffset % (2 * channels);
                        int copyLen = Math.Min(pcm.Length, Math.Max(0, output.Length - destByteOffset));
                        if (copyLen > 0) Array.Copy(pcm, 0, output, destByteOffset, copyLen);
                    }
                }
            }
            catch (Exception ex)
            {
                // Still best-effort -- whatever wasn't placed just stays silence, same as before --
                // but this used to swallow the exception completely, including a genuine OOM here
                // (this decodes a full clip's audio the same way ExportVideo decodes video, just PCM
                // instead of BGRA32). Logging it means a silent audio track in an export -- or in the
                // editor's own waveform preview, which hits this same kind of decode -- shows up as a
                // reason on disk instead of just quietly being wrong.
                CrashLogger.Log("VideoExportAudio", $"Skipping unreadable audio from '{clip.SourcePath}': {ex.GetType().Name}: {ex.Message}");
            }
            finally { reader?.Dispose(); }
        }
        return output;
    }

    private static void ApplyGain(byte[] pcm16, double gain)
    {
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            short s = BitConverter.ToInt16(pcm16, i);
            short g = (short)Math.Clamp(s * gain, short.MinValue, short.MaxValue);
            pcm16[i] = (byte)(g & 0xFF);
            pcm16[i + 1] = (byte)((g >> 8) & 0xFF);
        }
    }

    /// <summary>Crude nearest-neighbour resample by simply dropping/duplicating whole sample frames
    /// -- correct in the sense that the result's sample count matches its declared duration (so it
    /// stays in sync with the equally-retimed video), at the cost of shifting pitch along with speed,
    /// the same trade-off a basic "speed" control has always made.</summary>
    private static byte[] ResampleForSpeed(byte[] pcm16, int channels, double speedFactor)
    {
        int frameBytes = 2 * channels;
        int inFrames = pcm16.Length / frameBytes;
        int outFrames = Math.Max(1, (int)(inFrames / speedFactor));
        var result = new byte[outFrames * frameBytes];
        for (int i = 0; i < outFrames; i++)
        {
            int srcFrame = Math.Min(inFrames - 1, (int)(i * speedFactor));
            Array.Copy(pcm16, srcFrame * frameBytes, result, i * frameBytes, frameBytes);
        }
        return result;
    }

    /// <summary>A basic noise gate, not spectral denoising: any ~20ms window whose RMS amplitude is
    /// under 2% of the track's own peak gets silenced outright, which cleans up background
    /// hiss/hum between speech without attempting to separate noise from a wanted signal.</summary>
    private static void ApplyNoiseGate(byte[] pcm16)
    {
        const int windowSamples = 960;
        const double thresholdRatio = 0.02;

        int totalSamples = pcm16.Length / 2;
        if (totalSamples == 0) return;

        int peak = 1;
        for (int i = 0; i < totalSamples; i++)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm16, i * 2)));
        double threshold = peak * thresholdRatio;

        for (int start = 0; start < totalSamples; start += windowSamples)
        {
            int end = Math.Min(totalSamples, start + windowSamples);
            double sumSq = 0;
            for (int i = start; i < end; i++) { short s = BitConverter.ToInt16(pcm16, i * 2); sumSq += (double)s * s; }
            double rms = Math.Sqrt(sumSq / (end - start));
            if (rms < threshold)
                for (int i = start; i < end; i++) { pcm16[i * 2] = 0; pcm16[i * 2 + 1] = 0; }
        }
    }

    // ---- probing ----

    // EnableVideoProcessing lets the SourceReader insert a color-conversion MFT -- without it,
    // requesting RGB32 output fails outright for an H.264 source (MF_E_INVALIDMEDIATYPE): the
    // H.264 decoder's own native output is NV12, and only the video processor MFT can get from
    // there to RGB32. Every video-decoding SourceReader in this file needs this. (MF's "Rgb32" is
    // laid out in memory as BGRA -- matches the "bgra" pixel format ffmpeg is told to expect on its
    // raw video pipe input, see FfmpegEncodeSession.Start.)
    private static IMFAttributes VideoProcessingReaderAttrs()
    {
        var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true).CheckError();
        return attrs;
    }

    private static (int width, int height, int fps) ProbeVideoFormat(string sourcePath)
    {
        var reader = MediaFactory.MFCreateSourceReaderFromURL(sourcePath, VideoProcessingReaderAttrs());
        try
        {
            var desired = MediaFactory.MFCreateMediaType();
            var desiredAttrs = (IMFAttributes)desired;
            desiredAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            desiredAttrs.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, desired);

            var actual = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            var actualAttrs = (IMFAttributes)actual;
            MediaFactory.MFGetAttributeSize(actualAttrs, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h).CheckError();
            MediaFactory.MFGetAttributeRatio(actualAttrs, MediaTypeAttributeKeys.FrameRate, out uint num, out uint den).CheckError();
            int fps = den == 0 ? 30 : (int)Math.Round(num / (double)den);
            int width = (int)w - (int)w % 2, height = (int)h - (int)h % 2; // even dimensions -- yuv420p needs it
            return (width, height, Math.Max(1, fps));
        }
        finally { reader.Dispose(); }
    }

    private static (int sampleRate, int channels)? ProbeAudioFormat(string sourcePath)
    {
        IMFSourceReader? reader = null;
        try
        {
            reader = MediaFactory.MFCreateSourceReaderFromURL(sourcePath, null);
            var desired = MediaFactory.MFCreateMediaType();
            var desiredAttrs = (IMFAttributes)desired;
            desiredAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            desiredAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, desired);

            var actual = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            var actualAttrs = (IMFAttributes)actual;
            uint sr = MediaFactory.MFGetAttributeUInt32(actualAttrs, MediaTypeAttributeKeys.AudioSamplesPerSecond, 44100);
            uint ch = MediaFactory.MFGetAttributeUInt32(actualAttrs, MediaTypeAttributeKeys.AudioNumChannels, 2);
            return ((int)sr, (int)ch);
        }
        catch
        {
            return null; // no audio stream in this file
        }
        finally { reader?.Dispose(); }
    }

    private static int EstimateVideoBitrate(int width, int height, int fps) =>
        Math.Clamp((int)(width * height * fps * 0.1), 500_000, 8_000_000);
}
