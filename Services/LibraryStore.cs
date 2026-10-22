using System.Text.Json;
using GetSoundBoard.Models;

namespace GetSoundBoard.Services;

public sealed class LibraryStore
{
    private sealed class SavedData
    {
        public AppSettings Settings { get; set; } = new();
        public List<SoundClip> Clips { get; set; } = [];
    }

    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetSoundBoard");
    private string DataPath => Path.Combine(_root, "library.json");
    public string AudioFolder => Path.Combine(_root, "Audio");

    public (AppSettings Settings, List<SoundClip> Clips) Load()
    {
        Directory.CreateDirectory(AudioFolder);
        if (!File.Exists(DataPath)) return (new AppSettings(), []);
        try
        {
            var data = JsonSerializer.Deserialize<SavedData>(File.ReadAllText(DataPath), _json) ?? new SavedData();
            return (data.Settings ?? new AppSettings(), data.Clips ?? []);
        }
        catch (JsonException)
        {
            var backup = DataPath + ".damaged-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(DataPath, backup);
            throw new InvalidDataException($"A biblioteca não pôde ser lida. Uma cópia foi preservada em {backup}");
        }
    }

    public async Task SaveAsync(AppSettings settings, IEnumerable<SoundClip> clips)
    {
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(AudioFolder);
            var json = JsonSerializer.Serialize(new SavedData { Settings = settings, Clips = clips.ToList() }, _json);
            var temp = DataPath + ".tmp";
            await File.WriteAllTextAsync(temp, json);
            File.Move(temp, DataPath, true);
        }
        finally { _saveGate.Release(); }
    }

    public async Task<SoundClip> ImportAsync(string source)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".mp3" or ".wav"))
            throw new InvalidDataException("Use arquivos MP3 ou WAV.");

        var clip = new SoundClip { Title = Path.GetFileNameWithoutExtension(source).Trim() };
        if (clip.Title.Length == 0) clip.Title = "Sem nome";
        clip.FileName = clip.Id + extension;
        Directory.CreateDirectory(AudioFolder);
        var destination = Path.Combine(AudioFolder, clip.FileName);
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination + ".tmp");
        try
        {
            await input.CopyToAsync(output);
            await output.FlushAsync();
            output.Close();
            File.Move(destination + ".tmp", destination);
        }
        catch
        {
            output.Close();
            File.Delete(destination + ".tmp");
            throw;
        }
        return clip;
    }

    public string PathFor(SoundClip clip) => Path.Combine(AudioFolder, clip.FileName);

    public void DeleteFile(SoundClip clip)
    {
        var path = PathFor(clip);
        if (File.Exists(path)) File.Delete(path);
    }
}
