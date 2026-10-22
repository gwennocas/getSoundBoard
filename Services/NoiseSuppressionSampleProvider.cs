using NAudio.Wave;
using RNNoise.NET;

namespace GetSoundBoard.Services;

/// <summary>Processes only the microphone stream in 10 ms, 48 kHz mono frames.</summary>
public sealed class NoiseSuppressionSampleProvider : ISampleProvider, IDisposable
{
    private const int SamplesPerChannel = 480;
    private const int Channels = 2;
    private const int FrameSamples = SamplesPerChannel * Channels;
    private readonly ISampleProvider _source;
    private readonly Denoiser _left = new();
    private readonly Denoiser _right = new();
    private readonly float[] _frame = new float[FrameSamples];
    private readonly float[] _leftFrame = new float[SamplesPerChannel];
    private readonly float[] _rightFrame = new float[SamplesPerChannel];
    private readonly float[] _previousDryFrame = new float[FrameSamples];
    private int _framePosition = FrameSamples;
    private volatile bool _enabled;
    private volatile float _gain = 1f;
    private volatile float _strength = 0.5f;

    public NoiseSuppressionSampleProvider(ISampleProvider source, bool enabled, float gain = 1f, float strength = 0.5f)
    {
        if (source.WaveFormat.SampleRate != 48000 || source.WaveFormat.Channels != Channels ||
            source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("A voz deve estar em float estéreo a 48 kHz.", nameof(source));

        _source = source;
        _enabled = enabled;
        Gain = gain;
        Strength = strength;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public float Gain
    {
        get => _gain;
        set => _gain = float.IsFinite(value) ? Math.Clamp(value, 0f, 3f) : 1f;
    }

    public float Strength
    {
        get => _strength;
        set => _strength = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.5f;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if ((count & 1) != 0)
            throw new ArgumentException("A leitura estéreo precisa ter um número par de amostras.", nameof(count));

        var written = 0;
        while (written < count)
        {
            if (_framePosition == FrameSamples)
                ReadFrame();

            var take = Math.Min(count - written, FrameSamples - _framePosition);
            Array.Copy(_frame, _framePosition, buffer, offset + written, take);
            _framePosition += take;
            written += take;
        }
        return written;
    }

    private void ReadFrame()
    {
        var read = 0;
        while (read < FrameSamples)
        {
            var next = _source.Read(_frame, read, FrameSamples - read);
            if (next == 0) break;
            read += next;
        }
        if (read < FrameSamples)
            Array.Clear(_frame, read, FrameSamples - read);

        // Raising the microphone level here also makes a distant voice available to RNNoise.
        // The soundboard inputs are mixed later and never pass through this gain stage.
        var gain = _gain;
        if (gain != 1f)
            for (var i = 0; i < FrameSamples; i++)
                _frame[i] = Math.Clamp(_frame[i] * gain, -1f, 1f);

        var strength = _enabled ? _strength : 0f;
        if (strength > 0f)
        {
            for (var i = 0; i < SamplesPerChannel; i++)
            {
                _leftFrame[i] = _frame[i * Channels];
                _rightFrame[i] = _frame[i * Channels + 1];
            }
            _left.Denoise(_leftFrame);
            _right.Denoise(_rightFrame);
            for (var i = 0; i < SamplesPerChannel; i++)
            {
                // RNNoise outputs the previous 10 ms frame. Blend with the matching
                // delayed dry frame so partial suppression does not cause phasing.
                var leftIndex = i * Channels;
                var rightIndex = leftIndex + 1;
                var dryLeft = _previousDryFrame[leftIndex];
                var dryRight = _previousDryFrame[rightIndex];
                _previousDryFrame[leftIndex] = _frame[leftIndex];
                _previousDryFrame[rightIndex] = _frame[rightIndex];
                _frame[leftIndex] = dryLeft + (_leftFrame[i] - dryLeft) * strength;
                _frame[rightIndex] = dryRight + (_rightFrame[i] - dryRight) * strength;
            }
        }
        else
            Array.Copy(_frame, _previousDryFrame, FrameSamples);

        _framePosition = 0;
    }

    public void Dispose()
    {
        _left.Dispose();
        _right.Dispose();
    }
}
