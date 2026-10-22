namespace GetSoundBoard.Models;

public sealed class AppSettings
{
    public string MicrophoneId { get; set; } = "";
    public string SpeakerId { get; set; } = "";
    public string CableInputId { get; set; } = "";
    public double SpeakerVolume { get; set; } = 0.35;
    public double MicrophoneVolume { get; set; } = 0.70;
    public bool Onboarded { get; set; }
}
