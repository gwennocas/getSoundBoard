using GetSoundBoard.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GetSoundBoard.Services;

public sealed record AudioDeviceOption(string Id, string Name);

/// <summary>
/// Keeps two independent render streams running: sound effects only to the speakers,
/// and captured voice plus sound effects to the virtual cable.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private sealed class StandardFormatWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider _source;
        public WaveFormat WaveFormat { get; }

        public StandardFormatWaveProvider(IWaveProvider source)
        {
            _source = source;
            WaveFormat = source.WaveFormat.AsStandardWaveFormat();
            if (WaveFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
                throw new InvalidOperationException("O formato de captura do microfone não é PCM ou float.");
        }

        public int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, count);
    }

    private const int MixRate = 48000;
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _devices = new();
    private WasapiCapture? _microphone;
    private BufferedWaveProvider? _voiceBuffer;
    private WasapiOut? _speakerOutput;
    private WasapiOut? _cableOutput;
    private MixingSampleProvider? _speakerMix;
    private MixingSampleProvider? _cableMix;
    private WaveStream? _speakerReader;
    private WaveStream? _cableReader;
    private ISampleProvider? _speakerClipInput;
    private ISampleProvider? _cableClipInput;
    private VolumeSampleProvider? _speakerClipVolume;
    private VolumeSampleProvider? _cableClipVolume;
    private string _microphoneId = "";
    private string _speakerId = "";
    private string _cableId = "";

    public bool IsConfigured => _microphone is not null && _speakerOutput is not null && _cableOutput is not null;

    public IReadOnlyList<AudioDeviceOption> GetDevices(DataFlow flow) =>
        _devices.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDeviceOption(d.ID, d.FriendlyName)).ToList();

    public static void CheckFile(string path)
    {
        if (!AudioFileFormats.IsSupported(path))
            throw new InvalidDataException($"Formato não suportado. Use {AudioFileFormats.SupportedFormatsDescription}.");

        using var reader = AudioFileFormats.OpenReader(path);
        if (reader.WaveFormat.Channels is < 1 or > 2)
            throw new InvalidDataException("O áudio precisa ser mono ou estéreo.");
        if (reader.Length == 0)
            throw new InvalidDataException("O arquivo de áudio está vazio.");
    }

    public void Configure(AppSettings settings)
    {
        lock (_gate)
        {
            DisposeRoute();
            if (string.IsNullOrWhiteSpace(settings.MicrophoneId) ||
                string.IsNullOrWhiteSpace(settings.SpeakerId) ||
                string.IsNullOrWhiteSpace(settings.CableInputId))
                throw new InvalidOperationException("Escolha microfone, alto-falantes e VB-CABLE Input.");
            if (settings.SpeakerId == settings.CableInputId)
                throw new InvalidOperationException("Os alto-falantes e o VB-CABLE Input devem ser dispositivos diferentes.");

            try
            {
                var mic = _devices.GetDevice(settings.MicrophoneId);
                var speaker = _devices.GetDevice(settings.SpeakerId);
                var cable = _devices.GetDevice(settings.CableInputId);
                if (mic.State != DeviceState.Active || speaker.State != DeviceState.Active || cable.State != DeviceState.Active)
                    throw new InvalidOperationException("Um dos dispositivos de áudio está desconectado.");
                if (mic.DataFlow != DataFlow.Capture || speaker.DataFlow != DataFlow.Render || cable.DataFlow != DataFlow.Render)
                    throw new InvalidOperationException("Um dos dispositivos foi escolhido para a função errada.");
                if (!cable.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Escolha o dispositivo de reprodução CABLE Input do VB-CABLE.");

                var format = WaveFormat.CreateIeeeFloatWaveFormat(MixRate, 2);
                _speakerMix = new MixingSampleProvider(format) { ReadFully = true };
                _cableMix = new MixingSampleProvider(format) { ReadFully = true };

                _microphone = new WasapiCapture(mic);
                if (_microphone.WaveFormat.Channels is < 1 or > 2)
                    throw new InvalidOperationException("Este microfone usa mais de dois canais; escolha outro formato no Windows.");
                _voiceBuffer = new BufferedWaveProvider(_microphone.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                _microphone.DataAvailable += MicrophoneOnDataAvailable;
                var voice = ConvertToMixFormat(new StandardFormatWaveProvider(_voiceBuffer).ToSampleProvider());
                _cableMix.AddMixerInput(voice);

                _speakerOutput = new WasapiOut(speaker, AudioClientShareMode.Shared, true, 80);
                _cableOutput = new WasapiOut(cable, AudioClientShareMode.Shared, true, 80);
                _speakerOutput.Init(_speakerMix);
                _cableOutput.Init(_cableMix);
                _speakerOutput.Play();
                _cableOutput.Play();
                _microphone.StartRecording();
                _microphoneId = settings.MicrophoneId;
                _speakerId = settings.SpeakerId;
                _cableId = settings.CableInputId;
            }
            catch
            {
                DisposeRoute();
                throw;
            }
        }
    }

    private void MicrophoneOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try { _voiceBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded); }
        catch (Exception) { /* A route change can dispose the buffer mid-callback. */ }
    }

    public void PlayClip(string path, double speakerVolume, double microphoneVolume)
    {
        lock (_gate)
        {
            if (_speakerMix is null || _cableMix is null)
                throw new InvalidOperationException("Configure os dispositivos de áudio primeiro.");
            StopClipCore();
            try
            {
                _speakerReader = AudioFileFormats.OpenReader(path);
                _cableReader = AudioFileFormats.OpenReader(path);
                _speakerClipVolume = new VolumeSampleProvider(ConvertToMixFormat((ISampleProvider)_speakerReader))
                    { Volume = (float)Math.Clamp(speakerVolume, 0, 1) };
                _cableClipVolume = new VolumeSampleProvider(ConvertToMixFormat((ISampleProvider)_cableReader))
                    { Volume = (float)Math.Clamp(microphoneVolume, 0, 1) };
                _speakerClipInput = _speakerClipVolume;
                _cableClipInput = _cableClipVolume;
                _speakerMix.AddMixerInput(_speakerClipInput);
                _cableMix.AddMixerInput(_cableClipInput);
            }
            catch
            {
                StopClipCore();
                throw;
            }
        }
    }

    public void SetVolumes(double speakerVolume, double microphoneVolume)
    {
        lock (_gate)
        {
            if (_speakerClipVolume is not null) _speakerClipVolume.Volume = (float)Math.Clamp(speakerVolume, 0, 1);
            if (_cableClipVolume is not null) _cableClipVolume.Volume = (float)Math.Clamp(microphoneVolume, 0, 1);
        }
    }

    public bool ClipHasFinished
    {
        get
        {
            lock (_gate)
                return _speakerReader is not null && _cableReader is not null &&
                    _speakerReader.Position >= _speakerReader.Length && _cableReader.Position >= _cableReader.Length;
        }
    }

    public void StopClip()
    {
        lock (_gate) StopClipCore();
    }

    private void StopClipCore()
    {
        if (_speakerClipInput is not null) _speakerMix?.RemoveMixerInput(_speakerClipInput);
        if (_cableClipInput is not null) _cableMix?.RemoveMixerInput(_cableClipInput);
        _speakerClipInput = null;
        _cableClipInput = null;
        _speakerClipVolume = null;
        _cableClipVolume = null;
        _speakerReader?.Dispose();
        _cableReader?.Dispose();
        _speakerReader = null;
        _cableReader = null;
    }

    public bool DevicesAreAvailable()
    {
        if (!IsConfigured) return false;
        try
        {
            return _devices.GetDevice(_microphoneId).State == DeviceState.Active &&
                   _devices.GetDevice(_speakerId).State == DeviceState.Active &&
                   _devices.GetDevice(_cableId).State == DeviceState.Active &&
                   _speakerOutput?.PlaybackState == PlaybackState.Playing &&
                   _cableOutput?.PlaybackState == PlaybackState.Playing;
        }
        catch { return false; }
    }

    public void Disconnect()
    {
        lock (_gate) DisposeRoute();
    }

    private void DisposeRoute()
    {
        StopClipCore();
        if (_microphone is not null)
        {
            _microphone.DataAvailable -= MicrophoneOnDataAvailable;
            try { _microphone.StopRecording(); } catch { }
            _microphone.Dispose();
        }
        _microphone = null;
        _voiceBuffer = null;
        if (_speakerOutput is not null)
        {
            try { _speakerOutput.Stop(); } catch { }
            _speakerOutput.Dispose();
        }
        if (_cableOutput is not null)
        {
            try { _cableOutput.Stop(); } catch { }
            _cableOutput.Dispose();
        }
        _speakerOutput = null;
        _cableOutput = null;
        _speakerMix = null;
        _cableMix = null;
        _microphoneId = _speakerId = _cableId = "";
    }

    private static ISampleProvider ConvertToMixFormat(ISampleProvider input)
    {
        if (input.WaveFormat.Channels is < 1 or > 2)
            throw new InvalidDataException("O áudio precisa ser mono ou estéreo.");
        if (input.WaveFormat.SampleRate != MixRate)
            input = new WdlResamplingSampleProvider(input, MixRate);
        return input.WaveFormat.Channels == 1 ? new MonoToStereoSampleProvider(input) : input;
    }

    public void Dispose()
    {
        lock (_gate) DisposeRoute();
        _devices.Dispose();
    }
}
