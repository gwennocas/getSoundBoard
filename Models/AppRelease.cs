namespace GetSoundBoard.Models;

public sealed class AppRelease
{
    public required string Version { get; init; }
    public required string PublishedAt { get; init; }
    public required string StatusLabel { get; init; }
    public required string Notes { get; init; }
    public required string DownloadUrl { get; init; }
    public required string DownloadButtonText { get; init; }
}
