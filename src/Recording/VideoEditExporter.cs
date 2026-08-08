using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
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
/// Turns a <see cref="VideoEditPlan"/> into a real file by decoding each track's clips through Media
/// Foundation's SourceReader and re-encoding through a fresh SinkWriter -- the same H.264/AAC pipeline
/// <see cref="MfScreenRecorder"/> already uses for live recording, just fed from files instead of the
/// screen/mic directly.
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

    public static void Export(VideoEditPlan plan, IProgress<double>? progress, TimeSpan masterDuration, CancellationToken ct)
    {
        MediaFoundationBootstrap.EnsureStarted();

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
        // can be mono, an unusual rate, or otherwise something the AAC encoder MFT flatly rejects as
        // an *output* format (confirmed: a mono 22050Hz import made SetInputMediaType throw
        // MF_E_INVALIDMEDIATYPE). Fixing the output at a known AAC-safe format and letting
        // DecodeClipsToTimeline's forced resample (see its own comment) bring every clip -- original
        // or imported, whatever its native format -- into that same target sidesteps the whole class
        // of "which source's format wins" problem.
        if (systemClips.Count > 0 && ProbeAudioFormat(systemClips[0].SourcePath) == null) systemClips = new();
        if (micClips.Count > 0 && ProbeAudioFormat(micClips[0].SourcePath) == null) micClips = new();

        bool writeAudio = systemClips.Count > 0 || micClips.Count > 0;
        const int audioSampleRate = 48000, audioChannels = 2;

        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputPath)!);
        var (writer, videoStreamIdx, audioStreamIdx) = CreateSinkWriter(
            plan.OutputPath, width, height, fps, writeAudio ? audioSampleRate : null, writeAudio ? audioChannels : null, plan.QualityMultiplier);

        try
        {
            ExportVideo(plan, writer, videoStreamIdx, width, height, fps, masterDuration, progress, ct);

            if (writeAudio)
            {
                // ExportVideo just finished a long run of large (frame-sized) allocations -- even
                // with a single reused buffer for the decode loop itself, the video processor MFT,
                // encoder, and everything upstream of it still churn their own large buffers over
                // that same run. ExportMixedAudio is about to need one or two more big contiguous
                // managed allocations (each the length of the whole track). Rather than hope the
                // already-used LOH happens to have room, request one compacting collection right at
                // this natural boundary -- the standard fix for "big allocation right after a lot of
                // large-object churn" fragmentation, and cheap since it only runs once per export.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                ExportMixedAudio(plan, writer, audioStreamIdx, systemClips, micClips, audioSampleRate, audioChannels, masterDuration.Ticks, ct);
            }

            writer.Finalize();
        }
        finally
        {
            writer.Dispose();
        }
    }

    // ---- video: per-clip seek-once-then-linear-decode, filters, speed retiming ----

    private static void ExportVideo(VideoEditPlan plan, IMFSinkWriter writer, int streamIndex,
        int width, int height, int fps, TimeSpan masterDuration, IProgress<double>? progress, CancellationToken ct)
    {
        int stride = width * 4;
        int bufferSize = stride * height;
        bool needsFilters = plan.Brightness != 0 || plan.Contrast != 0 || plan.Saturation != 0 || plan.Grayscale;
        long frameDurTicks = Math.Max(1, TimeSpan.FromSeconds(1.0 / fps).Ticks);
        long masterTicks = masterDuration.Ticks;

        var clips = plan.VideoClips.Where(c => c.Duration > TimeSpan.Zero).OrderBy(c => c.TimelineStart).ToList();

        long outputCursor = 0; // next un-filled output tick (pre-speed), advances as clips/gaps are written
        int frameIndex = 0;

        // Encoder throughput has no dedicated diagnostic today -- a "stuck" export (WriteSample
        // blocking on a struggling/broken encoder MFT, e.g. a bad hardware transform) looks
        // identical to a healthy slow one from the outside until a user gives up waiting. Logging a
        // running fps figure every so often means the next report of "it's just sitting there" comes
        // with real throughput numbers instead of another guess.
        var exportStopwatch = Stopwatch.StartNew();
        void MaybeLogThroughput()
        {
            if (frameIndex == 0 || frameIndex % 300 != 0) return;
            double fps2 = frameIndex / Math.Max(0.001, exportStopwatch.Elapsed.TotalSeconds);
            CrashLogger.Log("VideoExportProgress",
                $"{frameIndex} frames written in {exportStopwatch.Elapsed:mm\\:ss} (~{fps2:0.0} fps effective encode rate).");
        }

        // One reusable scratch buffer for the whole export instead of `new byte[bufferSize]` per
        // frame: bufferSize is a full raw BGRA32 frame (~8MB at 1080p), well over the LOH threshold
        // (85,000 bytes), so allocating one per frame -- thousands of times for a multi-minute
        // recording -- churns the Large Object Heap. The LOH isn't compacted by default, so that
        // churn fragments the process's address space until a native Media Foundation allocation
        // (the encoder MFT, MFCreateMemoryBuffer) can't find a contiguous block and fails with
        // E_OUTOFMEMORY even though total free memory looks fine. Reusing one buffer turns that into
        // a single lived allocation for the entire export.
        var frameBuffer = new byte[bufferSize];

        void FillGapWithBlack(long uptoTicks)
        {
            if (outputCursor >= uptoTicks) return;
            var blank = new byte[bufferSize];
            for (; outputCursor < uptoTicks; outputCursor += frameDurTicks)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    WriteVideoBytes(writer, streamIndex, blank, bufferSize,
                        (long)(outputCursor / plan.SpeedFactor), (long)(frameDurTicks / plan.SpeedFactor));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogExportFailure("black gap fill", frameIndex, outputCursor, ex);
                    throw;
                }
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
                    var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out long ts);
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                    if (sample == null) continue;

                    using (sample)
                    {
                        if (ts < clip.SourceIn.Ticks) continue;
                        if (ts >= clip.SourceOut.Ticks) break;

                        long outputTicks = clip.TimelineStart.Ticks + (ts - clip.SourceIn.Ticks);
                        if (outputTicks < outputCursor) continue; // keep the written stream monotonic

                        long originalDuration = sample.SampleDuration;
                        using var buffer = sample.ConvertToContiguousBuffer();
                        buffer.Lock(out nint ptr, out _, out int length);
                        try { Marshal.Copy(ptr, frameBuffer, 0, Math.Min(length, bufferSize)); }
                        finally { buffer.Unlock(); }

                        if (needsFilters) ApplyFilters(frameBuffer, plan.Brightness, plan.Contrast, plan.Saturation, plan.Grayscale);

                        long newTime = (long)(outputTicks / plan.SpeedFactor);
                        long newDuration = (long)(originalDuration / plan.SpeedFactor);
                        try
                        {
                            WriteVideoBytes(writer, streamIndex, frameBuffer, bufferSize, newTime, newDuration);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            LogExportFailure(clip.SourcePath, frameIndex, outputTicks, ex);
                            throw;
                        }
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
    /// the exception itself into a plain-language message instead of the raw HRESULT text.</summary>
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

    private static void WriteVideoBytes(IMFSinkWriter writer, int streamIndex, byte[] bytes, int bufferSize, long sampleTime, long duration)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
        try
        {
            buffer.Lock(out nint ptr, out _, out _);
            try { Marshal.Copy(bytes, 0, ptr, bufferSize); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = bufferSize;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = sampleTime;
            sample.SampleDuration = duration;
            writer.WriteSample(streamIndex, sample);
        }
        finally { buffer.Dispose(); }
    }

    // ---- audio: mix mic + system, each track's clips decoded and placed independently ----

    private static void ExportMixedAudio(VideoEditPlan plan, IMFSinkWriter writer, int streamIndex,
        List<PlanClip> systemClips, List<PlanClip> micClips, int sampleRate, int channels, long masterDurationTicks, CancellationToken ct)
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
            // array just to hold the sum -- see AudioCaptureMixer.MixPcm16Into. Two duration-length
            // buffers alive at once instead of three, right where memory is already tightest.
            byte[] mixed;
            if (systemBuf != null && micBuf != null) { AudioCaptureMixer.MixPcm16Into(systemBuf, micBuf); mixed = systemBuf; }
            else mixed = systemBuf ?? micBuf ?? Array.Empty<byte>();

            if (Math.Abs(plan.MasterVolume - 1.0) > 0.001) ApplyGain(mixed, plan.MasterVolume);

            const int chunkBytes = 32 * 1024;
            long tick = 0;
            for (int offset = 0; offset < mixed.Length; offset += chunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                int len = Math.Min(chunkBytes, mixed.Length - offset);
                var chunk = new byte[len];
                Array.Copy(mixed, offset, chunk, 0, len);
                long durationTicks = (long)(len / (double)bytesPerSecond * TimeSpan.TicksPerSecond);
                WriteAudioBytes(writer, streamIndex, chunk, tick, durationTicks);
                tick += durationTicks;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogExportFailure($"audio mix (system clips: {systemClips.Count}, mic clips: {micClips.Count}, " +
                $"track length: {totalBytes / 1024.0 / 1024.0:0.0} MB)", -1, 0, ex);
            throw;
        }
    }

    /// <summary>Decodes one track's clips into a zero-filled (silence) buffer sized to the FINAL
    /// output length, seeking once per clip and placing each decoded sample at its mapped/speed-
    /// adjusted byte offset. Building each track's full timeline independently like this -- rather
    /// than trying to keep two live decode loops chunk-aligned -- is what lets system and mic (which
    /// may have entirely different clip arrangements after independent editing) still sum together
    /// correctly: MixPcm16 just adds two same-length buffers.</summary>
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
            catch { /* best effort -- whatever wasn't placed just stays silence */ }
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

    private static void WriteAudioBytes(IMFSinkWriter writer, int streamIndex, byte[] chunk, long sampleTime, long duration)
    {
        if (duration <= 0) return;
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
            sample.SampleDuration = duration;
            writer.WriteSample(streamIndex, sample);
        }
        finally { buffer.Dispose(); }
    }

    // ---- probing / sink writer setup ----

    // EnableVideoProcessing lets the SourceReader insert a color-conversion MFT -- without it,
    // requesting RGB32 output fails outright for an H.264 source (MF_E_INVALIDMEDIATYPE): the
    // H.264 decoder's own native output is NV12, and only the video processor MFT can get from
    // there to RGB32. Every video-decoding SourceReader in this file needs this.
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
            int width = (int)w - (int)w % 2, height = (int)h - (int)h % 2;
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

    private static (IMFSinkWriter writer, int videoStream, int audioStream) CreateSinkWriter(
        string outputPath, int width, int height, int fps, int? audioSampleRate, int? audioChannels, double qualityMultiplier = 1.0)
    {
        // DisableThrottling used to be set here -- it tells the SinkWriter to let WriteSample
        // return immediately instead of blocking/pacing to match how fast the encoder can actually
        // drain samples. Our decode loop feeds it frames much faster than realtime, and with nothing
        // throttling the producer, every WriteSample call's sample+native buffer piles up in the
        // SinkWriter's own internal queue rather than being freed once written -- invisible to the
        // managed heap (crash.log confirmed this: 41MB managed heap next to 27GB of process private
        // bytes at the moment of failure, dead a minute into an 8-minute export). Leaving throttling
        // at its default (enabled) makes WriteSample block until the encoder has caught up, which
        // bounds that queue -- slower wall-clock export time, but memory that stays flat instead of
        // climbing without bound.
        // ReadwriteEnableHardwareTransforms used to be true, letting the SinkWriter pick a
        // GPU/driver H.264 encoder MFT over the software one. With throttling now correctly applied
        // (see above), a report of export crawling for hours on a 6-minute recording pointed at the
        // same root: WriteSample now blocks on whatever encoder got chosen, and a hardware MFT that's
        // absent/broken/falling back badly for this driver would show up as exactly that -- near-total
        // stall -- instead of the unthrottled runaway queue it used to hide behind. The bundled
        // software H.264 encoder is slower per-frame on capable hardware, but it's the same MFT on
        // every machine regardless of GPU/driver, which is worth far more than hardware's speed upside
        // for a tool that has to just work on whatever the user has installed.
        var sinkAttrs = MediaFactory.MFCreateAttributes(1);
        sinkAttrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, false).CheckError();
        var writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, sinkAttrs);

        var outType = MediaFactory.MFCreateMediaType();
        var outAttrs = (IMFAttributes)outType;
        outAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        outAttrs.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(EstimateVideoBitrate(width, height, fps) * qualityMultiplier)).CheckError();
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

        int audioStream = -1;
        if (audioSampleRate is int sr && audioChannels is int ch)
        {
            var audioOutType = MediaFactory.MFCreateMediaType();
            var audioOutAttrs = (IMFAttributes)audioOutType;
            audioOutAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sr).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)ch).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(128_000 / 8)).CheckError();
            audioOutAttrs.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)16).CheckError();
            audioStream = writer.AddStream(audioOutType);

            var audioInType = MediaFactory.MFCreateMediaType();
            var audioInAttrs = (IMFAttributes)audioInType;
            audioInAttrs.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sr).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)ch).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)16).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(2 * ch)).CheckError();
            audioInAttrs.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(sr * 2 * ch)).CheckError();
            writer.SetInputMediaType(audioStream, audioInType, null);
        }

        writer.BeginWriting();
        return (writer, videoStream, audioStream);
    }

    private static int EstimateVideoBitrate(int width, int height, int fps) =>
        Math.Clamp((int)(width * height * fps * 0.1), 500_000, 8_000_000);
}
