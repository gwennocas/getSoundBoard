using NAudio.Wave;

namespace GetSoundBoard.Services;

/// <summary>Attenuates an output block only when its mixed peak would clip.</summary>
public sealed class PeakLimiterSampleProvider(ISampleProvider source) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var peak = 0f;
        for (var i = offset; i < offset + read; i++)
            peak = Math.Max(peak, Math.Abs(buffer[i]));

        if (peak > 0.98f)
        {
            var gain = 0.98f / peak;
            for (var i = offset; i < offset + read; i++)
                buffer[i] *= gain;
        }
        return read;
    }
}
