using System.Runtime.InteropServices;
using GetSoundBoard.Models;

namespace GetSoundBoard.Services;

public sealed class HotkeyManager : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const int StopId = 0;
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private readonly Dictionary<int, string> _clipIds = [];
    private int _nextId = 1;
    private bool _installed;

    public event Action<string>? Pressed;
    public event Action? StopRequested;

    public HotkeyManager(nint window)
    {
        _window = window;
        _callback = WindowProc;
        _installed = SetWindowSubclass(window, _callback, 1, 0);
        if (!_installed) throw new InvalidOperationException("Não foi possível ativar os atalhos globais.");
    }

    public IReadOnlyList<string> RegisterAll(IEnumerable<SoundClip> clips)
    {
        Clear();
        var errors = new List<string>();
        if (!RegisterHotKey(_window, StopId, ModControl | ModAlt | ModNoRepeat, 0x08))
            errors.Add("Ctrl+Alt+Backspace (Parar tudo) já está em uso.");
        foreach (var clip in clips.Where(c => !string.IsNullOrWhiteSpace(c.Hotkey)))
        {
            if (!TryParse(clip.Hotkey, out uint modifiers, out uint key))
            {
                errors.Add($"Atalho inválido em {clip.Title}: {clip.Hotkey}");
                continue;
            }
            var id = _nextId++;
            if (RegisterHotKey(_window, id, modifiers | ModNoRepeat, key))
                _clipIds[id] = clip.Id;
            else
                errors.Add($"{clip.Hotkey} já está em uso por outro aplicativo.");
        }
        return errors;
    }

    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            uint next = parts[i].ToLowerInvariant() switch
            {
                "ctrl" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                _ => 0
            };
            if (next == 0 || (modifiers & next) != 0) return false;
            modifiers |= next;
        }
        var name = parts[^1].ToUpperInvariant();
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
            key = name[0];
        else if (name.StartsWith('F') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 12)
            key = (uint)(0x70 + f - 1);
        return modifiers != 0 && key != 0;
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (message == WmHotkey && (int)wParam == StopId)
        {
            try { StopRequested?.Invoke(); } catch { }
            return 0;
        }
        if (message == WmHotkey && _clipIds.TryGetValue((int)wParam, out var clipId))
        {
            try { Pressed?.Invoke(clipId); } catch { }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Clear()
    {
        UnregisterHotKey(_window, StopId);
        foreach (var id in _clipIds.Keys) UnregisterHotKey(_window, id);
        _clipIds.Clear();
    }

    public void Dispose()
    {
        Clear();
        if (_installed) RemoveWindowSubclass(_window, _callback, 1);
        _installed = false;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
