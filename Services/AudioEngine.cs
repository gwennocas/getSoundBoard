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
    private sealed class ClipPlayback : IDisposable
    {
        public required string Path { get; init; }
        public required WaveStream SpeakerReader { get; init; }
        public required WaveStream CableReader { get; init; }
        public required VolumeSampleProvider SpeakerInput { get; init; }
        public required VolumeSampleProvider CableInput { get; init; }
        public EventHandler<SampleProviderEventArgs>? SpeakerEndedHandler { get; set; }
        public EventHandler<SampleProviderEventArgs>? CableEndedHandler { get; set; }
        public volatile bool SpeakerEnded;
        public volatile bool CableEnded;

        public bool IsFinished => SpeakerEnded && CableEnded;

        public void Dispose()
        {
            SpeakerReader.Dispose();
            CableReader.Dispose();
        }
    }

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
    private NoiseSuppressionSampleProvider? _voiceInput;
    private VoiceClaritySampleProvider? _voiceClarity;
    private readonly List<ClipPlayback> _activeClips = [];
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
                _voiceInput = new NoiseSuppressionSampleProvider(voice, settings.NoiseSuppressionEnabled,
                    (float)settings.MicrophoneGain, (float)settings.NoiseSuppressionStrength);
                _voiceClarity = new VoiceClaritySampleProvider(_voiceInput, settings.VoiceClarityEnabled);
                _cableMix.AddMixerInput(_voiceClarity);

                _speakerOutput = new WasapiOut(speaker, AudioClientShareMode.Shared, true, 80);
                _cableOutput = new WasapiOut(cable, AudioClientShareMode.Shared, true, 80);
                _speakerOutput.Init(new PeakLimiterSampleProvider(_speakerMix));
                _cableOutput.Init(new PeakLimiterSampleProvider(_cableMix));
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

    public void PlayClip(string path, double speakerVolume, double microphoneVolume, bool overlapSounds)
    {
        lock (_gate)
        {
            if (_speakerMix is null || _cableMix is null)
                throw new InvalidOperationException("Configure os dispositivos de áudio primeiro.");
            if (!overlapSounds) StopClipCore();
            WaveStream? speakerReader = null;
            WaveStream? cableReader = null;
            VolumeSampleProvider? speakerInput = null;
            VolumeSampleProvider? cableInput = null;
            ClipPlayback? clip = null;
            try
            {
                speakerReader = AudioFileFormats.OpenReader(path);
                cableReader = AudioFileFormats.OpenReader(path);
                speakerInput = new VolumeSampleProvider(ConvertToMixFormat(ToSampleProvider(speakerReader)))
                    { Volume = (float)Math.Clamp(speakerVolume, 0, 1) };
                cableInput = new VolumeSampleProvider(ConvertToMixFormat(ToSampleProvider(cableReader)))
                    { Volume = (float)Math.Clamp(microphoneVolume, 0, 1) };
                clip = new ClipPlayback
                {
                    Path = path,
                    SpeakerReader = speakerReader,
                    CableReader = cableReader,
                    SpeakerInput = speakerInput,
                    CableInput = cableInput
                };
                clip.SpeakerEndedHandler = (_, args) =>
                {
                    if (ReferenceEquals(args.SampleProvider, clip.SpeakerInput)) clip.SpeakerEnded = true;
                };
                clip.CableEndedHandler = (_, args) =>
                {
                    if (ReferenceEquals(args.SampleProvider, clip.CableInput)) clip.CableEnded = true;
                };
                _speakerMix.MixerInputEnded += clip.SpeakerEndedHandler;
                _cableMix.MixerInputEnded += clip.CableEndedHandler;
                _speakerMix.AddMixerInput(speakerInput);
                _cableMix.AddMixerInput(cableInput);
                _activeClips.Add(clip);
            }
            catch
            {
                if (clip?.SpeakerEndedHandler is not null) _speakerMix.MixerInputEnded -= clip.SpeakerEndedHandler;
                if (clip?.CableEndedHandler is not null) _cableMix.MixerInputEnded -= clip.CableEndedHandler;
                if (speakerInput is not null) _speakerMix.RemoveMixerInput(speakerInput);
                if (cableInput is not null) _cableMix.RemoveMixerInput(cableInput);
                speakerReader?.Dispose();
                cableReader?.Dispose();
                throw;
            }
        }
    }

    public void SetVolumes(double speakerVolume, double microphoneVolume)
    {
        lock (_gate)
        {
            foreach (var clip in _activeClips)
            {
                clip.SpeakerInput.Volume = (float)Math.Clamp(speakerVolume, 0, 1);
                clip.CableInput.Volume = (float)Math.Clamp(microphoneVolume, 0, 1);
            }
        }
    }

    public void SetNoiseSuppression(bool enabled)
    {
        lock (_gate)
            if (_voiceInput is not null) _voiceInput.Enabled = enabled;
    }

    public void SetMicrophoneGain(double gain)
    {
        lock (_gate)
            if (_voiceInput is not null) _voiceInput.Gain = (float)gain;
    }

    public void SetNoiseSuppressionStrength(double strength)
    {
        lock (_gate)
            if (_voiceInput is not null) _voiceInput.Strength = (float)strength;
    }

    public void SetVoiceClarity(bool enabled)
    {
        lock (_gate)
            if (_voiceClarity is not null) _voiceClarity.Enabled = enabled;
    }

    public void SetOverlapSounds(bool enabled)
    {
        if (enabled) return;
        lock (_gate)
            foreach (var clip in _activeClips.Take(Math.Max(0, _activeClips.Count - 1)).ToArray())
                RemoveClipCore(clip);
    }

    public bool RemoveFinishedClips()
    {
        lock (_gate)
        {
            var finished = _activeClips.Where(c => c.IsFinished).ToArray();
            foreach (var clip in finished)
                RemoveClipCore(clip);
            return finished.Length > 0 && _activeClips.Count == 0;
        }
    }

    public void StopClip(string path)
    {
        lock (_gate)
            foreach (var clip in _activeClips.Where(c => c.Path == path).ToArray())
                RemoveClipCore(clip);
    }

    public void StopClip()
    {
        lock (_gate) StopClipCore();
    }

    private void StopClipCore()
    {
        foreach (var clip in _activeClips.ToArray())
            RemoveClipCore(clip);
    }

    private void RemoveClipCore(ClipPlayback clip)
    {
        if (clip.SpeakerEndedHandler is not null && _speakerMix is not null)
            _speakerMix.MixerInputEnded -= clip.SpeakerEndedHandler;
        if (clip.CableEndedHandler is not null && _cableMix is not null)
            _cableMix.MixerInputEnded -= clip.CableEndedHandler;
        _speakerMix?.RemoveMixerInput(clip.SpeakerInput);
        _cableMix?.RemoveMixerInput(clip.CableInput);
        _activeClips.Remove(clip);
        clip.Dispose();
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
        _voiceInput?.Dispose();
        _voiceInput = null;
        _voiceClarity = null;
        _speakerMix = null;
        _cableMix = null;
        _microphoneId = _speakerId = _cableId = "";
    }

    private static ISampleProvider ToSampleProvider(WaveStream reader) =>
        reader is ISampleProvider sampleProvider ? sampleProvider : reader.ToSampleProvider();

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
