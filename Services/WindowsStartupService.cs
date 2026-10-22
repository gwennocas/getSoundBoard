using Microsoft.Win32;

namespace GetSoundBoard.Services;

/// <summary>Registers the unpackaged app for the current user's Windows sign-in.</summary>
public sealed class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "getSoundBoard";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("Não foi possível abrir a inicialização do Windows.");

        if (enabled)
            key.SetValue(ValueName, GetStartupCommand(), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, false);
    }

    public void UpdateExecutablePathIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key?.GetValue(ValueName) is not string value) return;

        var command = GetStartupCommand();
        if (!string.Equals(value, command, StringComparison.OrdinalIgnoreCase))
            key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string GetStartupCommand()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !string.Equals(Path.GetFileName(executable), "getSoundBoard.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Execute o getSoundBoard.exe para ativar a inicialização com o Windows.");

        return $"\"{executable}\" --startup";
    }
}
