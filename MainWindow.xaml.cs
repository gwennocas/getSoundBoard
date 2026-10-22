using System.Collections.ObjectModel;
using System.Diagnostics;
using GetSoundBoard.Models;
using GetSoundBoard.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace GetSoundBoard;

public sealed partial class MainWindow : Window
{
    private readonly LibraryStore _store = new();
    private readonly AudioEngine _audio = new();
    private readonly List<SoundClip> _clips = [];
    private readonly ObservableCollection<SoundClip> _visibleClips = [];
    private readonly HotkeyManager _hotkeys;
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly nint _hwnd;
    private AppSettings _settings = new();
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hotkeys = new HotkeyManager(_hwnd);
        _hotkeys.Pressed += PlayById;
        _hotkeys.StopRequested += StopPlayback;
        SoundGrid.ItemsSource = _visibleClips;
        Closed += MainWindow_Closed;
        _healthTimer.Tick += HealthTimer_Tick;

        try
        {
            var saved = _store.Load();
            _settings = saved.Settings;
            _clips.AddRange(saved.Clips);
        }
        catch (Exception ex)
        {
            SetStatus($"Biblioteca não carregada: {ex.Message}", true);
        }

        SpeakerVolumeSlider.Value = Math.Clamp(_settings.SpeakerVolume * 100, 0, 100);
        MicVolumeSlider.Value = Math.Clamp(_settings.MicrophoneVolume * 100, 0, 100);
        SpeakerPercentText.Text = $"{SpeakerVolumeSlider.Value:0}%";
        MicPercentText.Text = $"{MicVolumeSlider.Value:0}%";
        FirstRunHint.Visibility = _settings.Onboarded ? Visibility.Collapsed : Visibility.Visible;
        RefreshVisibleClips();
        RefreshDevices();

        if (_settings.Onboarded)
        {
            try
            {
                _audio.Configure(_settings);
                SetStatus("Áudio conectado. Escolha um som ou use um atalho.");
            }
            catch (Exception ex)
            {
                SetStatus($"Verifique a configuração de áudio: {ex.Message}", true);
            }
        }
        RegisterHotkeys();
        _initialized = true;
        _healthTimer.Start();
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            error ? Windows.UI.Color.FromArgb(255, 255, 155, 165) : Windows.UI.Color.FromArgb(255, 169, 176, 192));
    }

    private void RefreshVisibleClips()
    {
        var query = SearchBox.Text.Trim();
        _visibleClips.Clear();
        foreach (var clip in _clips.Where(c => c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            _visibleClips.Add(clip);
        EmptyState.Visibility = _visibleClips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDevices()
    {
        try
        {
            var microphoneOptions = _audio.GetDevices(DataFlow.Capture)
                .Where(d => !d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)).ToList();
            var outputOptions = _audio.GetDevices(DataFlow.Render);
            var speakerOptions = outputOptions
                .Where(d => !d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)).ToList();
            var cableOptions = outputOptions
                .Where(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)).ToList();
            MicrophoneBox.ItemsSource = microphoneOptions;
            SpeakerBox.ItemsSource = speakerOptions;
            CableBox.ItemsSource = cableOptions;
            MicrophoneBox.SelectedValue = microphoneOptions.Any(d => d.Id == _settings.MicrophoneId)
                ? _settings.MicrophoneId : microphoneOptions.FirstOrDefault()?.Id;
            SpeakerBox.SelectedValue = speakerOptions.Any(d => d.Id == _settings.SpeakerId)
                ? _settings.SpeakerId : speakerOptions.FirstOrDefault()?.Id;
            CableBox.SelectedValue = cableOptions.Any(d => d.Id == _settings.CableInputId)
                ? _settings.CableInputId : cableOptions.FirstOrDefault()?.Id;
            if (cableOptions.Count == 0)
                SetStatus("VB-CABLE não encontrado. Instale pelo link, reinicie o Windows e atualize a lista.", true);
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível listar os dispositivos: {ex.Message}", true);
        }
    }

    private void RegisterHotkeys()
    {
        var errors = _hotkeys.RegisterAll(_clips);
        if (errors.Count > 0) SetStatus(string.Join("  ", errors), true);
    }

    private async Task PersistAsync()
    {
        try { await _store.SaveAsync(_settings, _clips); }
        catch (Exception ex) { SetStatus($"Não foi possível salvar a biblioteca: {ex.Message}", true); }
    }

    private async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        var count = 0;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                AudioEngine.CheckFile(path);
                _clips.Add(await _store.ImportAsync(path));
                count++;
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (count > 0)
        {
            RefreshVisibleClips();
            await PersistAsync();
        }
        SetStatus(errors.Count > 0 ? string.Join("  ", errors) : $"{count} áudio(s) adicionado(s).", errors.Count > 0);
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".wav");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await ImportFilesAsync(files.Select(f => f.Path));
        }
        catch (Exception ex) { SetStatus($"Falha ao importar: {ex.Message}", true); }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            await ImportFilesAsync(items.OfType<StorageFile>().Select(f => f.Path));
        }
        catch (Exception ex) { SetStatus($"Falha ao importar: {ex.Message}", true); }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) RefreshVisibleClips();
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) PlayById(id);
    }

    private void PlayById(string id)
    {
        var clip = _clips.FirstOrDefault(c => c.Id == id);
        if (clip is null) return;
        try
        {
            _audio.PlayClip(_store.PathFor(clip), _settings.SpeakerVolume, _settings.MicrophoneVolume);
            SetStatus($"Tocando: {clip.Title}");
        }
        catch (Exception ex) { SetStatus($"Não foi possível tocar {clip.Title}: {ex.Message}", true); }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
        => StopPlayback();

    private void StopPlayback()
    {
        _audio.StopClip();
        SetStatus("Reprodução parada. Sua voz continua conectada.");
    }

    private async void EditClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var clip = _clips.FirstOrDefault(c => c.Id == id);
        if (clip is null) return;

        var title = new TextBox { Text = clip.Title, Header = "Nome" };
        var modifiers = new ComboBox { Header = "Combinação", ItemsSource = new[] { "Sem atalho", "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var keys = new ComboBox { Header = "Tecla", ItemsSource = HotkeyKeys(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var parts = clip.Hotkey.Split('+');
        var currentModifiers = parts.Length >= 2 ? string.Join("+", parts.SkipLast(1)) : "Sem atalho";
        modifiers.SelectedItem = new[] { "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift" }.Contains(currentModifiers) ? currentModifiers : "Sem atalho";
        keys.SelectedItem = parts.Length >= 2 ? parts[^1] : "F1";
        if (keys.SelectedItem is null) keys.SelectedItem = "F1";
        var form = new StackPanel { Spacing = 14 };
        form.Children.Add(title);
        form.Children.Add(modifiers);
        form.Children.Add(keys);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Editar som",
            Content = form,
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var newTitle = title.Text.Trim();
        if (newTitle.Length == 0) { SetStatus("Digite um nome para o som.", true); return; }
        var newHotkey = (string?)modifiers.SelectedItem == "Sem atalho" ? "" : $"{modifiers.SelectedItem}+{keys.SelectedItem}";
        if (newHotkey.Length > 0 && _clips.Any(c => c.Id != clip.Id && string.Equals(c.Hotkey, newHotkey, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"{newHotkey} já pertence a outro som.", true);
            return;
        }
        clip.Title = newTitle;
        clip.Hotkey = newHotkey;
        RegisterHotkeys();
        RefreshVisibleClips();
        await PersistAsync();
    }

    private static string[] HotkeyKeys() =>
        Enumerable.Range('A', 26).Select(i => ((char)i).ToString())
            .Concat(Enumerable.Range(0, 10).Select(i => i.ToString()))
            .Concat(Enumerable.Range(1, 12).Select(i => $"F{i}"))
            .ToArray();

    private async void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var clip = _clips.FirstOrDefault(c => c.Id == id);
        if (clip is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Excluir som?",
            Content = $"{clip.Title} será removido da biblioteca e o arquivo importado será apagado.",
            PrimaryButtonText = "Excluir",
            CloseButtonText = "Cancelar"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            _audio.StopClip();
            _store.DeleteFile(clip);
            _clips.Remove(clip);
            RefreshVisibleClips();
            RegisterHotkeys();
            await PersistAsync();
            SetStatus($"{clip.Title} excluído.");
        }
        catch (Exception ex) { SetStatus($"Não foi possível excluir: {ex.Message}", true); }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private async void ApplyAudio_Click(object sender, RoutedEventArgs e)
    {
        var micId = MicrophoneBox.SelectedValue as string;
        var speakerId = SpeakerBox.SelectedValue as string;
        var cableId = CableBox.SelectedValue as string;
        if (micId is null || speakerId is null || cableId is null)
        {
            SetStatus("Escolha os três dispositivos. Se o cabo não aparece, instale o VB-CABLE e atualize.", true);
            return;
        }
        var next = new AppSettings
        {
            MicrophoneId = micId,
            SpeakerId = speakerId,
            CableInputId = cableId,
            SpeakerVolume = SpeakerVolumeSlider.Value / 100,
            MicrophoneVolume = MicVolumeSlider.Value / 100,
            Onboarded = true
        };
        try
        {
            _audio.Configure(next);
            _settings = next;
            FirstRunHint.Visibility = Visibility.Collapsed;
            await PersistAsync();
            SetStatus("Áudio conectado. No Discord/jogo, selecione CABLE Output como microfone.");
        }
        catch (Exception ex) { SetStatus($"Não foi possível conectar o áudio: {ex.Message}", true); }
    }

    private async void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized) return;
        SpeakerPercentText.Text = $"{SpeakerVolumeSlider.Value:0}%";
        MicPercentText.Text = $"{MicVolumeSlider.Value:0}%";
        _settings.SpeakerVolume = SpeakerVolumeSlider.Value / 100;
        _settings.MicrophoneVolume = MicVolumeSlider.Value / 100;
        _audio.SetVolumes(_settings.SpeakerVolume, _settings.MicrophoneVolume);
        await PersistAsync();
    }

    private void CableLink_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/index.htm") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"Não foi possível abrir o site: {ex.Message}", true); }
    }

    private void HealthTimer_Tick(object? sender, object e)
    {
        if (_audio.IsConfigured && !_audio.DevicesAreAvailable())
        {
            _audio.Disconnect();
            SetStatus("Um dispositivo de áudio foi desconectado. Atualize a lista, confira as escolhas e clique em Aplicar.", true);
        }
        else if (_audio.ClipHasFinished)
        {
            _audio.StopClip();
            SetStatus("Pronto para o próximo som.");
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _healthTimer.Stop();
        _hotkeys.Dispose();
        _audio.Dispose();
    }
}
