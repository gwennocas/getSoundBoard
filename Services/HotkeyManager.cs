using System.Runtime.InteropServices;
using GetSoundBoard.Models;

namespace GetSoundBoard.Services;

public sealed class HotkeyManager : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint LlkhfExtended = 0x0001;
    private const int WhKeyboardLl = 13;
    private const int StopId = 0;
    private readonly nint _window;
    private readonly uint _numpadMessage = RegisterWindowMessage("getSoundBoard.NumpadHotkey");
    private readonly SubclassProc _callback;
    private readonly LowLevelKeyboardProc _keyboardCallback;
    private readonly Dictionary<int, string> _clipIds = [];
    private readonly Dictionary<int, string> _numpadClipIds = [];
    private readonly Dictionary<(uint Modifiers, string Key), int> _numpadCombinations = [];
    private readonly HashSet<int> _reservedIds = [];
    private readonly HashSet<string> _heldNumpadKeys = [];
    private int _nextId = 1;
    private nint _keyboardHook;
    private bool _installed;

    public event Action<string>? Pressed;
    public event Action? StopRequested;

    public HotkeyManager(nint window)
    {
        _window = window;
        _callback = WindowProc;
        _keyboardCallback = KeyboardProc;
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

            var name = clip.Hotkey.Split('+')[^1].Trim();
            var id = _nextId++;
            if (TryGetNumpadKey(name, out var numpadKey))
            {
                var combination = (modifiers, numpadKey!);
                if (_numpadCombinations.ContainsKey(combination))
                {
                    errors.Add($"{clip.Hotkey} já pertence a outro som.");
                    continue;
                }

                // Reserve distinct VKs to report external conflicts. NumEnter has only VK_RETURN.
                if (numpadKey != "NumEnter" && !RegisterHotKey(_window, id, modifiers | ModNoRepeat, key))
                {
                    errors.Add($"{clip.Hotkey} já está em uso por outro aplicativo.");
                    continue;
                }
                if (numpadKey != "NumEnter") _reservedIds.Add(id);
                _numpadCombinations[combination] = id;
                _numpadClipIds[id] = clip.Id;
            }
            else if (RegisterHotKey(_window, id, modifiers | ModNoRepeat, key))
                _clipIds[id] = clip.Id;
            else
                errors.Add($"{clip.Hotkey} já está em uso por outro aplicativo.");
        }

        if (_numpadCombinations.Count > 0)
        {
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, GetModuleHandle(null), 0);
            if (_keyboardHook == 0)
            {
                errors.Add("Não foi possível ativar os atalhos do teclado numérico.");
                foreach (var id in _reservedIds) UnregisterHotKey(_window, id);
                _reservedIds.Clear();
                _numpadCombinations.Clear();
                _numpadClipIds.Clear();
            }
        }
        return errors;
    }

    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
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

        var name = parts[^1];
        if (TryGetNumpadKey(name, out var numpadKey))
            key = NumpadVirtualKey(numpadKey);
        else if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
            key = char.ToUpperInvariant(name[0]);
        else if (name.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(name[1..], out var f) && f is >= 1 and <= 12)
            key = (uint)(0x70 + f - 1);

        return key != 0 && (modifiers != 0 || numpadKey is not null);
    }

    public static bool IsNumpadKey(string name) => TryGetNumpadKey(name, out _);

    private static bool TryGetNumpadKey(string name, out string? normalized)
    {
        normalized = name.ToUpperInvariant() switch
        {
            "NUM0" => "Num0", "NUM1" => "Num1", "NUM2" => "Num2",
            "NUM3" => "Num3", "NUM4" => "Num4", "NUM5" => "Num5",
            "NUM6" => "Num6", "NUM7" => "Num7", "NUM8" => "Num8",
            "NUM9" => "Num9", "NUMDECIMAL" => "NumDecimal",
            "NUMADD" => "NumAdd", "NUMSUBTRACT" => "NumSubtract",
            "NUMMULTIPLY" => "NumMultiply", "NUMDIVIDE" => "NumDivide",
            "NUMENTER" => "NumEnter", _ => null
        };
        return normalized is not null;
    }

    private static uint NumpadVirtualKey(string? key) => key switch
    {
        "Num0" => 0x60, "Num1" => 0x61, "Num2" => 0x62,
        "Num3" => 0x63, "Num4" => 0x64, "Num5" => 0x65,
        "Num6" => 0x66, "Num7" => 0x67, "Num8" => 0x68,
        "Num9" => 0x69, "NumMultiply" => 0x6A, "NumAdd" => 0x6B,
        "NumSubtract" => 0x6D, "NumDecimal" => 0x6E,
        "NumDivide" => 0x6F, "NumEnter" => 0x0D, _ => 0
    };

    private nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
            var key = PhysicalNumpadKey(data.ScanCode, (data.Flags & LlkhfExtended) != 0);
            if (key is not null)
            {
                var message = (uint)wParam;
                if (message is WmKeyUp or WmSysKeyUp)
                    _heldNumpadKeys.Remove(key);
                else if ((message is WmKeyDown or WmSysKeyDown) && _heldNumpadKeys.Add(key))
                {
                    var modifiers = CurrentModifiers();
                    if (_numpadCombinations.TryGetValue((modifiers, key), out var id))
                        PostMessage(_window, _numpadMessage, (nint)id, 0);
                }
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static uint CurrentModifiers()
    {
        uint modifiers = 0;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= ModControl;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= ModAlt;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= ModShift;
        return modifiers;
    }

    private static string? PhysicalNumpadKey(uint scanCode, bool extended) => (scanCode, extended) switch
    {
        (0x52, false) => "Num0", (0x4F, false) => "Num1",
        (0x50, false) => "Num2", (0x51, false) => "Num3",
        (0x4B, false) => "Num4", (0x4C, false) => "Num5",
        (0x4D, false) => "Num6", (0x47, false) => "Num7",
        (0x48, false) => "Num8", (0x49, false) => "Num9",
        (0x53, false) => "NumDecimal", (0x4E, false) => "NumAdd",
        (0x4A, false) => "NumSubtract", (0x37, false) => "NumMultiply",
        (0x35, true) => "NumDivide", (0x1C, true) => "NumEnter",
        _ => null
    };

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (message == WmHotkey && (int)wParam == StopId)
        {
            try { StopRequested?.Invoke(); } catch { }
            return 0;
        }
        if ((message == WmHotkey && _clipIds.TryGetValue((int)wParam, out var clipId)) ||
            (message == _numpadMessage && _numpadClipIds.TryGetValue((int)wParam, out clipId)))
        {
            try { Pressed?.Invoke(clipId); } catch { }
            return 0;
        }
        if (message == WmHotkey && _reservedIds.Contains((int)wParam)) return 0;
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Clear()
    {
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = 0;
        _heldNumpadKeys.Clear();
        UnregisterHotKey(_window, StopId);
        foreach (var id in _clipIds.Keys) UnregisterHotKey(_window, id);
        foreach (var id in _reservedIds) UnregisterHotKey(_window, id);
        _clipIds.Clear();
        _reservedIds.Clear();
        _numpadClipIds.Clear();
        _numpadCombinations.Clear();
    }

    public void Dispose()
    {
        Clear();
        if (_installed) RemoveWindowSubclass(_window, _callback, 1);
        _installed = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdllHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
