using NAudio.Vorbis;
using NAudio.Wave;

namespace GetSoundBoard.Services;

public static class AudioFileFormats
{
    private static readonly string[] SupportedExtensions =
    [
        ".mp3", ".wav", ".ogg", ".oga", ".opus", ".flac", ".aiff", ".aif", ".m4a", ".aac", ".wma"
    ];

    public const string SupportedFormatsDescription = "MP3, WAV, OGG/Vorbis e Ogg/Opus, FLAC, AIFF, M4A, AAC e WMA";

    public static IReadOnlyList<string> Extensions => SupportedExtensions;

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static WaveStream OpenReader(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            || ((extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                 || extension.Equals(".oga", StringComparison.OrdinalIgnoreCase))
                && IsOggOpusFile(path)))
            return new OpusOggWaveReader(path);

        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".oga", StringComparison.OrdinalIgnoreCase))
            return new VorbisWaveReader(path);

        // AudioFileReader uses Media Foundation on Windows for formats beyond WAV/MP3/AIFF.
        // Windows codecs may vary by edition and installation, so import validation opens the file first.
        return new AudioFileReader(path);
    }

    private static bool IsOggOpusFile(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[(int)Math.Min(stream.Length, 65536)];
        var read = stream.Read(header, 0, header.Length);
        return header.AsSpan(0, read).IndexOf("OpusHead"u8) >= 0;
    }
}
