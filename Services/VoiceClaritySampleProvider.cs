using NAudio.Dsp;
using NAudio.Wave;

namespace GetSoundBoard.Services;

/// <summary>Subtle speech EQ on the microphone path, after noise suppression.</summary>
public sealed class VoiceClaritySampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BiQuadFilter[] _highPass = new BiQuadFilter[2];
    private readonly BiQuadFilter[] _presence = new BiQuadFilter[2];
    private volatile bool _enabled;

    public VoiceClaritySampleProvider(ISampleProvider source, bool enabled)
    {
        if (source.WaveFormat.Channels != 2 || source.WaveFormat.SampleRate != 48000)
            throw new ArgumentException("O áudio da voz deve estar em estéreo a 48 kHz.", nameof(source));

        _source = source;
        _enabled = enabled;
        for (var channel = 0; channel < 2; channel++)
        {
            _highPass[channel] = BiQuadFilter.HighPassFilter(48000, 85, 0.707f);
            _presence[channel] = BiQuadFilter.PeakingEQ(48000, 3000, 0.9f, 2.5f);
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var enabled = _enabled;
        for (var i = 0; i < read; i++)
        {
            var channel = i & 1;
            // Keep filter state warm even while bypassed for a clean live toggle.
            var filtered = _presence[channel].Transform(_highPass[channel].Transform(buffer[offset + i]));
            if (enabled) buffer[offset + i] = filtered;
        }
        return read;
    }
}
