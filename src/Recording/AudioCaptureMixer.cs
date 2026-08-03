using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

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
            _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5),
            };
            _micCapture.DataAvailable += (_, e) => _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
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
            _systemCapture.DataAvailable += (_, e) => _systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
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

    public void Start()
    {
        _micCapture?.StartRecording();
        _systemCapture?.StartRecording();
    }

    public void Stop()
    {
        try { _micCapture?.StopRecording(); } catch { /* already stopped/device gone */ }
        try { _systemCapture?.StopRecording(); } catch { /* already stopped/device gone */ }
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
    /// Also reused by <see cref="Recording.VideoEditExporter"/> at export time to re-mix the
    /// mic/system sidecar tracks at the editor's chosen volumes.</summary>
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

    public void Dispose()
    {
        _micCapture?.Dispose();
        _systemCapture?.Dispose();
        (_systemResampled as IDisposable)?.Dispose();
    }
}
