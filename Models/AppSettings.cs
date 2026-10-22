namespace GetSoundBoard.Models;

public sealed class AppSettings
{
    public string MicrophoneId { get; set; } = "";
    public string SpeakerId { get; set; } = "";
    public string CableInputId { get; set; } = "";
    public double SpeakerVolume { get; set; } = 0.35;
    public double MicrophoneVolume { get; set; } = 0.70;
    public double MicrophoneGain { get; set; } = 1.0;
    public bool NoiseSuppressionEnabled { get; set; }
    public double NoiseSuppressionStrength { get; set; } = 0.5;
    public bool VoiceClarityEnabled { get; set; }
    public bool OverlapSounds { get; set; }
    public bool Onboarded { get; set; }
}
