using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SnapDoc.Services;

namespace SnapDoc.Recording;

/// <summary>
/// Captures microphone and/or system-output (loopback) audio via WASAPI and hands the recorder
/// fixed-size chunks of 16-bit PCM, mixed together if both sources are active.
///
/// Real hardware here reports its native mix format as 48kHz/stereo/32-bit IEEE float (confirmed
/// against this machine's actual devices before wiring this in), not 16-bit PCM, so every chunk is
/// converted on the way out. If mic and system audio end up with different native formats, the
/// system-audio side is resampled to match the mic's format via <see cref="MediaFoundationResampler"/>
/// (Windows' own resampler, wrapped by NAudio) before mixing -- letting Windows do that conversion
/// rather than hand-rolling one.
/// </summary>
public sealed class AudioCaptureMixer : IDisposable
{
    private readonly WasapiCapture? _micCapture;
    private readonly WasapiLoopbackCapture? _systemCapture;
    private readonly BufferedWaveProvider? _micBuffer;
    private readonly BufferedWaveProvider? _systemBuffer;
    private readonly IWaveProvider? _systemResampled;
    private readonly WaveFormat _outputFormat;

    // Diagnostic only -- a report of "recording has no audio" turned out to be a genuinely-silent
    // capture (verified independently via ffmpeg's volumedetect: -91dB mean/max on a real recording,
    // i.e. the mixing/writing pipeline was faithfully encoding actual silence, not losing/corrupting
    // real audio) rather than a bug anywhere in this class's own logic. That points at WASAPI itself
    // not delivering real samples -- most commonly Windows' microphone privacy toggle being off for
    // desktop apps, which doesn't make OpenStream/StartRecording fail, it just delivers a continuous
    // stream of zeroed buffers as if there were only silence to capture. These counters make that
    // distinguishable from an actual SnapDoc bug (DataAvailable never firing at all, or firing but the
    // buffer being genuinely non-zero yet still ending up silent downstream) on the next report,
    // without needing to reproduce it here first.
    private long _micCallbacks, _micBytes;
    private bool _micSawNonZero;
    private long _systemCallbacks, _systemBytes;
    private bool _systemSawNonZero;

    public int SampleRate => _outputFormat.SampleRate;
    public int Channels => _outputFormat.Channels;

    /// <summary>Mutes that source's contribution to the mix without stopping/restarting its WASAPI
    /// stream -- keeps capture continuous (and the two streams time-aligned) while recording,
    /// which a real stop/start of the device would risk disturbing.</summary>
    public bool MicMuted { get; set; }
    public bool SystemMuted { get; set; }

    public AudioCaptureMixer(string? micDeviceId, bool micEnabled, bool systemAudioEnabled)
    {
        if (!micEnabled && !systemAudioEnabled)
            throw new ArgumentException("At least one audio source must be enabled.");

        using var enumerator = new MMDeviceEnumerator();

        if (micEnabled)
        {
            var micDevice = string.IsNullOrEmpty(micDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(micDeviceId);
            _micCapture = new WasapiCapture(micDevice);
            CrashLogger.Log("AudioCapture",
                $"Mic device: '{micDevice.FriendlyName}' (state: {micDevice.State}, requested id: '{micDeviceId}'), " +
                $"format: {_micCapture.WaveFormat}");
            _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5),
            };
            _micCapture.DataAvailable += (_, e) =>
            {
                _micCallbacks++;
                _micBytes += e.BytesRecorded;
                if (!_micSawNonZero && ContainsNonZero(e.Buffer, e.BytesRecorded)) _micSawNonZero = true;
                _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
        }

        if (systemAudioEnabled)
        {
            var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _systemCapture = new WasapiLoopbackCapture(renderDevice);
            _systemBuffer = new BufferedWaveProvider(_systemCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5),
            };
            _systemCapture.DataAvailable += (_, e) =>
            {
                _systemCallbacks++;
                _systemBytes += e.BytesRecorded;
                if (!_systemSawNonZero && ContainsNonZero(e.Buffer, e.BytesRecorded)) _systemSawNonZero = true;
                _systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
        }

        // Mic quality/rate matters more when it's present (voiceover is the point); otherwise
        // system audio's own format is the target.
        _outputFormat = _micCapture?.WaveFormat ?? _systemCapture!.WaveFormat;

        if (_systemBuffer != null && _micCapture != null && !FormatsMatch(_systemCapture!.WaveFormat, _outputFormat))
            _systemResampled = new MediaFoundationResampler(_systemBuffer, _outputFormat) { ResamplerQuality = 60 };
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b) =>
        a.SampleRate == b.SampleRate && a.Channels == b.Channels &&
        a.BitsPerSample == b.BitsPerSample && a.Encoding == b.Encoding;

    private static bool ContainsNonZero(byte[] buffer, int count)
    {
        // Only the first chunk of each callback -- this runs on WASAPI's own capture thread at a
        // steady cadence, and finding out "is there ANY real signal at all" doesn't need to scan
        // every byte of every callback for the entire recording, just enough to catch it once.
        int scan = Math.Min(count, 4096);
        for (int i = 0; i < scan; i++)
            if (buffer[i] != 0) return true;
        return false;
    }

    public void Start()
    {
        try { _micCapture?.StartRecording(); }
        catch (Exception ex)
        {
            CrashLogger.Log("AudioCapture", $"Mic StartRecording threw: {ex}");
            throw;
        }
        try { _systemCapture?.StartRecording(); }
        catch (Exception ex)
        {
            CrashLogger.Log("AudioCapture", $"System-audio StartRecording threw: {ex}");
            throw;
        }
    }

    public void Stop()
    {
        try { _micCapture?.StopRecording(); } catch { /* already stopped/device gone */ }
        try { _systemCapture?.StopRecording(); } catch { /* already stopped/device gone */ }

        // Logged here (not per-callback) so it's one readable line per recording. A genuinely silent
        // capture -- zero callbacks, or callbacks that only ever delivered zero bytes -- points at
        // WASAPI/Windows (most likely the microphone privacy toggle for desktop apps), not a bug in
        // this class's own mixing/writing logic, which was independently verified to be encoding
        // whatever it's handed correctly (see the field comment on the counters above).
        if (_micCapture != null)
            CrashLogger.Log("AudioCapture",
                $"Mic capture summary: {_micCallbacks} callbacks, {_micBytes} bytes, " +
                $"{(_micSawNonZero ? "contained real (non-zero) audio data" : "EVERY CALLBACK WAS SILENT (all-zero) -- check Windows microphone privacy settings / correct device selected")}");
        if (_systemCapture != null)
            CrashLogger.Log("AudioCapture",
                $"System audio capture summary: {_systemCallbacks} callbacks, {_systemBytes} bytes, " +
                $"{(_systemSawNonZero ? "contained real (non-zero) audio data" : "EVERY CALLBACK WAS SILENT (all-zero)")}");
    }

    /// <summary>Pull the next chunk of mixed 16-bit PCM at (SampleRate, Channels), converting from
    /// whichever native format each source captured in. Always returns exactly byteCount bytes
    /// (silence-padded) as long as at least one source is active, so the caller's timestamp math
    /// stays simple. Called on the recorder's own audio thread at a steady cadence.</summary>
    public byte[] ReadMixedPcm16(int byteCount) => ReadAllPcm16(byteCount).Mixed;

    /// <summary>Same pull as <see cref="ReadMixedPcm16"/>, but also hands back the two pre-mix
    /// chunks (already normalized to 16-bit PCM at <see cref="SampleRate"/>/<see cref="Channels"/>)
    /// so a caller that wants independent mic/system tracks -- not just the mixed one -- doesn't
    /// have to read the underlying WASAPI buffers a second time (they're drain-once; reading twice
    /// per cycle would desync the two sources against each other and against the mixed track).
    /// Null for whichever source wasn't enabled for this recording.</summary>
    public (byte[] Mixed, byte[]? Mic, byte[]? System) ReadAllPcm16(int byteCount)
    {
        byte[]? micChunk = _micBuffer != null ? ReadExact(_micBuffer, byteCount) : null;
        byte[]? sysChunk = _systemResampled != null ? ReadExact(_systemResampled, byteCount)
                          : _systemBuffer != null ? ReadExact(_systemBuffer, byteCount)
                          : null;

        // Still drain the buffers above even when muted (Read() has to keep being called or the
        // ring buffer backs up) -- just zero out what we drained before it's used.
        if (MicMuted && micChunk != null) Array.Clear(micChunk);
        if (SystemMuted && sysChunk != null) Array.Clear(sysChunk);

        // sysChunk (whether passed through untouched or resampled) is always already in
        // _outputFormat by construction -- see the constructor.
        byte[]? micPcm = micChunk != null ? ToPcm16(micChunk, _micCapture!.WaveFormat) : null;
        byte[]? sysPcm = sysChunk != null ? ToPcm16(sysChunk, _outputFormat) : null;

        byte[] mixed = micPcm != null && sysPcm != null ? MixPcm16(micPcm, sysPcm)
                      : micPcm != null ? micPcm
                      : sysPcm != null ? sysPcm
                      : Array.Empty<byte>();

        return (mixed, micPcm, sysPcm);
    }

    private static byte[] ReadExact(IWaveProvider source, int byteCount)
    {
        var buffer = new byte[byteCount];
        // BufferedWaveProvider (and MediaFoundationResampler wrapping one) defaults to
        // ReadFully=true: Read() always fills the whole buffer, padding with silence if the
        // hardware hasn't delivered enough yet -- exactly what keeps chunk duration math simple.
        source.Read(buffer, 0, byteCount);
        return buffer;
    }

    private static byte[] ToPcm16(byte[] raw, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16) return raw;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32) return FloatToPcm16(raw);
        // Unsupported native format (rare on modern WASAPI endpoints) -- emit silence rather than
        // reinterpret bytes incorrectly and produce noise.
        return new byte[raw.Length / (format.BitsPerSample / 8) * 2];
    }

    private static byte[] FloatToPcm16(byte[] floatBytes)
    {
        int n = floatBytes.Length / 4;
        var pcm = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            float f = BitConverter.ToSingle(floatBytes, i * 4);
            short s = (short)(Math.Clamp(f, -1f, 1f) * short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>Sums two already-16-bit-PCM buffers sample-by-sample, clamped against overflow.
    /// Live capture chunks are small (a few hundred ms), so a fresh output array per call here is
    /// fine -- <see cref="Recording.VideoEditExporter"/> uses <see cref="MixPcm16Into"/> instead for
    /// its own duration-length buffers, where a third full-size allocation just to hold the sum is
    /// the difference between two big buffers alive at once and three.</summary>
    internal static byte[] MixPcm16(byte[] aPcm, byte[] bPcm)
    {
        int n = Math.Min(aPcm.Length, bPcm.Length) / 2;
        var mixed = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short a = BitConverter.ToInt16(aPcm, i * 2);
            short b = BitConverter.ToInt16(bPcm, i * 2);
            short clamped = (short)Math.Clamp(a + b, short.MinValue, short.MaxValue);
            mixed[i * 2] = (byte)(clamped & 0xFF);
            mixed[i * 2 + 1] = (byte)((clamped >> 8) & 0xFF);
        }
        return mixed;
    }

    /// <summary>Same sum as <see cref="MixPcm16"/>, written back into <paramref name="target"/>
    /// in place instead of returning a new array -- see <see cref="Recording.VideoEditExporter"/>'s
    /// ExportMixedAudio, the only caller, for why avoiding that third allocation matters there.</summary>
    internal static void MixPcm16Into(byte[] target, byte[] addPcm)
    {
        int n = Math.Min(target.Length, addPcm.Length) / 2;
        for (int i = 0; i < n; i++)
        {
            short a = BitConverter.ToInt16(target, i * 2);
            short b = BitConverter.ToInt16(addPcm, i * 2);
            short clamped = (short)Math.Clamp(a + b, short.MinValue, short.MaxValue);
            target[i * 2] = (byte)(clamped & 0xFF);
            target[i * 2 + 1] = (byte)((clamped >> 8) & 0xFF);
        }
    }

    public void Dispose()
    {
        _micCapture?.Dispose();
        _systemCapture?.Dispose();
        (_systemResampled as IDisposable)?.Dispose();
    }
}
