using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GetSoundBoard.Models;

public sealed class SoundClip : INotifyPropertyChanged
{
    private string _title = "Novo som";
    private string _hotkey = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";

    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    public string Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
