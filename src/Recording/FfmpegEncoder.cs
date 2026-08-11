using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SnapDoc.Recording;

/// <summary>
/// A single running ffmpeg encode: owns the child process and its stdin pipe (raw video frames go
/// there), and knows how to finish cleanly (close stdin, wait for the process to actually finalize
/// the output file) or tear down early on cancellation/failure. stderr is drained continuously on a
/// background thread via ErrorDataReceived/BeginErrorReadLine from the moment the process starts --
/// never read synchronously after the fact -- so a chatty ffmpeg can't fill its pipe buffer and
/// deadlock the export the way an unread stream would (exactly the class of bug a naive
/// Process.Start + WaitForExit() would risk).
/// </summary>
internal sealed class FfmpegEncodeSession : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderrTail = new();
    private readonly object _stderrLock = new();
    private bool _stdinClosed;

    /// <summary>Raw byte stream to write BGRA32 video frames into, in display order, tightly packed
    /// (no row padding) -- exactly what ExportVideo already produces into its reused frame buffer.
    /// Writing here blocks once the OS pipe's own (small, fixed) kernel buffer fills and ffmpeg
    /// hasn't drained it yet -- real backpressure, not a setting that might or might not be honored,
    /// which is the whole reason this replaced Media Foundation's SinkWriter.</summary>
    public Stream VideoInput { get; }

    private FfmpegEncodeSession(Process process)
    {
        _process = process;
        VideoInput = process.StandardInput.BaseStream;
    }

    internal static FfmpegEncodeSession Start(int width, int height, double framerate, string? audioWavPath,
        string outputPath, int videoBitrate, string videoEncoder)
    {
        var psi = new ProcessStartInfo(FfmpegEncoder.ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");
        // Video: raw BGRA32 frames piped in on stdin, tightly packed, no container/codec of its own.
        // framerate is fps*SpeedFactor, not just the source's own fps -- see VideoEditExporter's
        // ExportVideoToFfmpeg comment on why speed changes are expressed this way with a raw pipe
        // input instead of Media Foundation's old per-sample-timestamp approach.
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pixel_format"); psi.ArgumentList.Add("bgra");
        psi.ArgumentList.Add("-video_size"); psi.ArgumentList.Add($"{width}x{height}");
        psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add(framerate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");

        if (audioWavPath != null)
        {
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audioWavPath);
        }

        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add(videoEncoder);
        if (videoEncoder == "h264_nvenc") { psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("p4"); }
        psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add(videoBitrate.ToString());
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");

        if (audioWavPath != null)
        {
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-shortest"); // video/audio decode independently; keep them from drifting apart by a stray sample or two
        }
        else
        {
            psi.ArgumentList.Add("-an");
        }

        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(outputPath);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start ffmpeg.exe");
        var session = new FfmpegEncodeSession(process);

        process.ErrorDataReceived += (_, e) => session.AppendStderr(e.Data);
        process.OutputDataReceived += (_, _) => { }; // ffmpeg writes little/nothing to stdout here; drained anyway so it can never back up
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        return session;
    }

    private void AppendStderr(string? line)
    {
        if (line == null) return;
        lock (_stderrLock)
        {
            _stderrTail.AppendLine(line);
            // Bounded regardless of how long the export runs -- this is for the tail of a failure
            // message, not a full log (that's what CrashLogger.Log gets fed separately).
            const int maxChars = 8000;
            if (_stderrTail.Length > maxChars) _stderrTail.Remove(0, _stderrTail.Length - maxChars);
        }
    }

    private string GetStderrTail() { lock (_stderrLock) return _stderrTail.ToString(); }

    /// <summary>Closes stdin (signals "no more frames") and waits for ffmpeg to actually finish
    /// finalizing the output file, staying responsive to cancellation instead of blocking forever --
    /// a cancelled export kills the process outright rather than waiting for it to drain whatever's
    /// still in flight.</summary>
    public void FinishAndThrowIfFailed(CancellationToken ct)
    {
        CloseStdin();
        while (!_process.WaitForExit(200))
        {
            if (ct.IsCancellationRequested)
            {
                Kill();
                ct.ThrowIfCancellationRequested();
            }
        }
        if (_process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {_process.ExitCode}.\n{GetStderrTail()}");
    }

    /// <summary>Best-effort tail of ffmpeg's own diagnostic output, for logging alongside whatever
    /// exception actually propagates (e.g. a broken pipe from writing to VideoInput after ffmpeg
    /// already exited/crashed -- the exception itself won't say why ffmpeg quit, but this will).</summary>
    public string DiagnosticTail() => GetStderrTail();

    private void CloseStdin()
    {
        if (_stdinClosed) return;
        _stdinClosed = true;
        try { VideoInput.Flush(); } catch { /* ffmpeg may have already exited/closed its end */ }
        try { VideoInput.Close(); } catch { }
    }

    public void Cancel() => Kill();

    private void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        CloseStdin();
        try { _process.Dispose(); } catch { }
    }
}

/// <summary>
/// Bundled ffmpeg.exe (see ThirdParty/ffmpeg/NOTICE.txt), used only by <see cref="VideoEditExporter"/>
/// as its encode step. Chooses the fastest H.264 encoder this specific machine can actually use --
/// not just "is it listed by ffmpeg -encoders" (a GPU-less machine still lists h264_nvenc/qsv/amf,
/// they just fail the moment you try to use them) -- by attempting a real, tiny, throwaway encode
/// with each hardware candidate in turn and using whichever one actually works, falling back to the
/// bundled software encoder (libopenh264 -- not libx264: this build is the LGPL variant, which
/// excludes GPL-only components, see the NOTICE file) if none do.
/// </summary>
internal static class FfmpegEncoder
{
    public static string ExecutablePath { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    public static bool IsAvailable => File.Exists(ExecutablePath);

    private static string? _cachedEncoder;
    private static readonly object _detectLock = new();

    /// <summary>Result cached for the process's lifetime -- the available hardware doesn't change
    /// mid-session, and each probe is a real (if tiny) process launch not worth repeating per export.</summary>
    public static string DetectBestVideoEncoder()
    {
        lock (_detectLock)
        {
            if (_cachedEncoder != null) return _cachedEncoder;

            foreach (var candidate in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            {
                if (TryEncoder(candidate)) { _cachedEncoder = candidate; return candidate; }
            }
            _cachedEncoder = "libopenh264";
            return _cachedEncoder;
        }
    }

    private static bool TryEncoder(string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(ExecutablePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("testsrc=duration=0.2:size=64x64:rate=5");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add(encoder);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var process = Process.Start(psi);
            if (process == null) return false;

            // Both streams drained concurrently via async reads before the blocking WaitForExit below
            // -- this probe's output is tiny either way, but the pattern is the same one the real
            // encode session uses and there's no reason to risk it even here.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            stdoutTask.Wait(1000);
            stderrTask.Wait(1000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
