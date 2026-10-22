using System.Collections.ObjectModel;
using System.Diagnostics;
using GetSoundBoard.Models;
using GetSoundBoard.Services;
using Microsoft.UI.Windowing;
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
    private readonly UpdateService _updateService = new();
    private readonly WindowsStartupService _windowsStartup = new();
    private readonly ObservableCollection<AppRelease> _releases = [];
    private TrayService? _tray;
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly nint _hwnd;
    private AppSettings _settings = new();
    private bool _initialized;
    private bool _hiddenInTray;
    private bool _exitRequested;
    private bool _updatesLoaded;
    private bool _updatingStartupSwitch;
    private DateTimeOffset _startupAudioRetryUntil;
    private DateTimeOffset _nextStartupAudioAttempt;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hotkeys = new HotkeyManager(_hwnd);
        _hotkeys.Pressed += PlayById;
        _hotkeys.StopRequested += StopPlayback;
        SoundGrid.ItemsSource = _visibleClips;
        ReleaseList.ItemsSource = _releases;
        CurrentVersionText.Text = _updateService.CurrentVersionText;
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
        VoiceGainSlider.Value = Math.Clamp(_settings.MicrophoneGain * 100, 0, 300);
        NoiseStrengthSlider.Value = Math.Clamp(_settings.NoiseSuppressionStrength * 100, 0, 100);
        NoiseSuppressionSwitch.IsOn = _settings.NoiseSuppressionEnabled;
        VoiceClaritySwitch.IsOn = _settings.VoiceClarityEnabled;
        OverlapSoundsSwitch.IsOn = _settings.OverlapSounds;
        try
        {
            _windowsStartup.UpdateExecutablePathIfEnabled();
            StartWithWindowsSwitch.IsOn = _windowsStartup.IsEnabled;
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível verificar a inicialização com o Windows: {ex.Message}", true);
        }
        SpeakerPercentText.Text = $"{SpeakerVolumeSlider.Value:0}%";
        MicPercentText.Text = $"{MicVolumeSlider.Value:0}%";
        VoiceGainPercentText.Text = $"{VoiceGainSlider.Value:0}%";
        NoiseStrengthPercentText.Text = $"{NoiseStrengthSlider.Value:0}%";
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
        try
        {
            _tray = new TrayService(
                () => DispatcherQueue.TryEnqueue(ShowFromTray),
                () => DispatcherQueue.TryEnqueue(StopPlayback),
                () => DispatcherQueue.TryEnqueue(ExitApplication));
            AppWindow.Closing += AppWindow_Closing;
            AppWindow.Changed += AppWindow_Changed;
        }
        catch (Exception ex)
        {
            SetStatus($"Ícone da área de notificação indisponível: {ex.Message}", true);
        }
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
            var microphoneOptions = _audio.GetDevices(DataFlow.Capture);
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
                SetStatus("VB-CABLE não encontrado. Baixe e extraia o ZIP oficial, execute o instalador x64 como administrador, reinicie o Windows e clique em Atualizar dispositivos.", true);
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
            foreach (var extension in AudioFileFormats.Extensions)
                picker.FileTypeFilter.Add(extension);
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
            _audio.PlayClip(_store.PathFor(clip), _settings.SpeakerVolume, _settings.MicrophoneVolume, _settings.OverlapSounds);
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
        var modifierChoices = new[] { "Sem atalho", "Nenhum", "Ctrl", "Alt", "Shift", "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift", "Ctrl+Alt+Shift" };
        var modifiers = new ComboBox { Header = "Modificadores", ItemsSource = modifierChoices, HorizontalAlignment = HorizontalAlignment.Stretch };
        var keys = new ComboBox { Header = "Teclas comuns", ItemsSource = HotkeyKeys(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var parts = clip.Hotkey.Split('+');
        var selectedKey = clip.Hotkey.Length == 0 ? "F1" : parts[^1];
        var currentModifiers = clip.Hotkey.Length == 0 ? "Sem atalho" :
            parts.Length == 1 ? "Nenhum" : string.Join("+", parts.SkipLast(1));
        modifiers.SelectedItem = modifierChoices.Contains(currentModifiers) ? currentModifiers : "Sem atalho";

        var normalBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 49, 57, 76));
        var selectedBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 91, 96, 215));
        var numpadButtons = new Dictionary<string, Button>();
        var numpad = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var i = 0; i < 4; i++)
        {
            numpad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            numpad.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        }
        var numpadLayout = new (string Code, string Label)[]
        {
            ("Num7", "7"), ("Num8", "8"), ("Num9", "9"), ("NumDivide", "/"),
            ("Num4", "4"), ("Num5", "5"), ("Num6", "6"), ("NumMultiply", "*"),
            ("Num1", "1"), ("Num2", "2"), ("Num3", "3"), ("NumSubtract", "−"),
            ("Num0", "0"), ("NumDecimal", ". / ,"), ("NumAdd", "+"), ("NumEnter", "Enter")
        };
        for (var i = 0; i < numpadLayout.Length; i++)
        {
            var (code, label) = numpadLayout[i];
            var button = new Button
            {
                Content = label,
                Tag = code,
                Background = normalBrush,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0)
            };
            button.Click += (_, _) =>
            {
                selectedKey = code;
                keys.SelectedItem = null;
                foreach (var pair in numpadButtons)
                    pair.Value.Background = pair.Key == code ? selectedBrush : normalBrush;
            };
            Grid.SetRow(button, i / 4);
            Grid.SetColumn(button, i % 4);
            numpad.Children.Add(button);
            numpadButtons.Add(code, button);
        }
        keys.SelectionChanged += (_, _) =>
        {
            if (keys.SelectedItem is not string key) return;
            selectedKey = key;
            foreach (var button in numpadButtons.Values) button.Background = normalBrush;
        };
        if (HotkeyManager.IsNumpadKey(selectedKey))
            numpadButtons[selectedKey].Background = selectedBrush;
        else
        {
            keys.SelectedItem = selectedKey;
            if (keys.SelectedItem is null) keys.SelectedItem = "F1";
        }

        var numpadGroup = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 40, 56)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Child = new StackPanel { Spacing = 9 }
        };
        var numpadContent = (StackPanel)numpadGroup.Child;
        numpadContent.Children.Add(new TextBlock { Text = "Teclado numérico", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        numpadContent.Children.Add(numpad);
        numpadContent.Children.Add(new TextBlock
        {
            Text = "As teclas físicas do numpad funcionam também com NumLock desligado. Conflitos externos de NumEnter não podem ser detectados.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });

        var form = new StackPanel { Spacing = 14 };
        form.Children.Add(title);
        form.Children.Add(modifiers);
        form.Children.Add(keys);
        form.Children.Add(numpadGroup);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Editar som",
            Content = new ScrollViewer { Content = form, MaxHeight = 560 },
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var newTitle = title.Text.Trim();
        if (newTitle.Length == 0) { SetStatus("Digite um nome para o som.", true); return; }
        var selectedModifiers = (string?)modifiers.SelectedItem;
        var newHotkey = selectedModifiers == "Sem atalho" ? "" :
            selectedModifiers == "Nenhum" ? selectedKey : $"{selectedModifiers}+{selectedKey}";
        if (newHotkey.Length > 0 && !HotkeyManager.TryParse(newHotkey, out _, out _))
        {
            SetStatus("Escolha um atalho válido. Teclas comuns precisam de Ctrl, Alt ou Shift.", true);
            return;
        }
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
            _audio.StopClip(_store.PathFor(clip));
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
            SetStatus("Escolha os três dispositivos. Para instalar o VB-CABLE: baixe e extraia o ZIP oficial, execute o instalador x64 como administrador, reinicie o Windows e atualize a lista.", true);
            return;
        }
        var next = new AppSettings
        {
            MicrophoneId = micId,
            SpeakerId = speakerId,
            CableInputId = cableId,
            SpeakerVolume = SpeakerVolumeSlider.Value / 100,
            MicrophoneVolume = MicVolumeSlider.Value / 100,
            MicrophoneGain = VoiceGainSlider.Value / 100,
            NoiseSuppressionEnabled = NoiseSuppressionSwitch.IsOn,
            NoiseSuppressionStrength = NoiseStrengthSlider.Value / 100,
            VoiceClarityEnabled = VoiceClaritySwitch.IsOn,
            OverlapSounds = OverlapSoundsSwitch.IsOn,
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

    private async void NoiseSuppressionSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.NoiseSuppressionEnabled = NoiseSuppressionSwitch.IsOn;
        _audio.SetNoiseSuppression(_settings.NoiseSuppressionEnabled);
        await PersistAsync();
    }

    private async void VoiceGainSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized) return;
        VoiceGainPercentText.Text = $"{VoiceGainSlider.Value:0}%";
        _settings.MicrophoneGain = VoiceGainSlider.Value / 100;
        _audio.SetMicrophoneGain(_settings.MicrophoneGain);
        await PersistAsync();
    }

    private async void NoiseStrengthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized) return;
        NoiseStrengthPercentText.Text = $"{NoiseStrengthSlider.Value:0}%";
        _settings.NoiseSuppressionStrength = NoiseStrengthSlider.Value / 100;
        _audio.SetNoiseSuppressionStrength(_settings.NoiseSuppressionStrength);
        await PersistAsync();
    }

    private async void VoiceClaritySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.VoiceClarityEnabled = VoiceClaritySwitch.IsOn;
        _audio.SetVoiceClarity(_settings.VoiceClarityEnabled);
        await PersistAsync();
    }

    private async void OverlapSoundsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.OverlapSounds = OverlapSoundsSwitch.IsOn;
        _audio.SetOverlapSounds(_settings.OverlapSounds);
        await PersistAsync();
    }

    private void StartWithWindowsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingStartupSwitch) return;
        try
        {
            _windowsStartup.SetEnabled(StartWithWindowsSwitch.IsOn);
            SetStatus(StartWithWindowsSwitch.IsOn
                ? "O getSoundBoard iniciará na área de notificação com o Windows."
                : "Inicialização com o Windows desligada.");
        }
        catch (Exception ex)
        {
            _updatingStartupSwitch = true;
            try
            {
                try { StartWithWindowsSwitch.IsOn = _windowsStartup.IsEnabled; }
                catch { StartWithWindowsSwitch.IsOn = !StartWithWindowsSwitch.IsOn; }
            }
            finally { _updatingStartupSwitch = false; }
            SetStatus($"Não foi possível alterar a inicialização com o Windows: {ex.Message}", true);
        }
    }

    private void MicVolumeDown_Click(object sender, RoutedEventArgs e)
        => MicVolumeSlider.Value = Math.Max(0, MicVolumeSlider.Value - 5);

    private void MicVolumeUp_Click(object sender, RoutedEventArgs e)
        => MicVolumeSlider.Value = Math.Min(100, MicVolumeSlider.Value + 5);

    private void CableLink_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"Não foi possível abrir o site: {ex.Message}", true); }
    }

    private async void MainTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(MainTabView.SelectedItem, UpdatesTab) && !_updatesLoaded)
            await LoadUpdatesAsync();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        => await LoadUpdatesAsync();

    private async Task LoadUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdatesLoadingRing.IsActive = true;
        NoReleasesState.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Consultando versões publicadas no GitHub…";
        UpdateStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 169, 176, 192));

        try
        {
            var releases = await _updateService.GetReleasesAsync();
            _releases.Clear();
            foreach (var release in releases)
                _releases.Add(release);

            _updatesLoaded = true;
            var newerCount = releases.Count(release => release.StatusLabel == "Atualização disponível");
            UpdateStatusText.Text = newerCount > 0
                ? $"Encontramos {newerCount} versão(ões) mais recente(s). Escolha uma para baixar o instalador."
                : releases.Count == 0
                    ? "Nenhuma versão estável publicada foi encontrada."
                    : "Você está com a versão mais recente disponível.";
            NoReleasesState.Visibility = releases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _updatesLoaded = false;
            UpdateStatusText.Text = $"Não foi possível consultar as versões. Verifique sua conexão e tente de novo. ({ex.Message})";
            UpdateStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 255, 155, 165));
        }
        finally
        {
            UpdatesLoadingRing.IsActive = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void DownloadRelease_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "github.com")
        {
            SetStatus("O link de download desta versão não é válido.", true);
            return;
        }

        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"Não foi possível abrir o download: {ex.Message}", true); }
    }

    private void HealthTimer_Tick(object? sender, object e)
    {
        if (_audio.IsConfigured && !_audio.DevicesAreAvailable())
        {
            _audio.Disconnect();
            SetStatus("Um dispositivo de áudio foi desconectado. Atualize a lista, confira as escolhas e clique em Aplicar.", true);
        }
        else if (_audio.RemoveFinishedClips())
        {
            SetStatus("Pronto para o próximo som.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!_audio.IsConfigured && _settings.Onboarded && now < _startupAudioRetryUntil &&
            now >= _nextStartupAudioAttempt)
        {
            _nextStartupAudioAttempt = now.AddSeconds(5);
            try
            {
                _audio.Configure(_settings);
                _startupAudioRetryUntil = DateTimeOffset.MinValue;
                SetStatus("Áudio conectado após a inicialização do Windows.");
            }
            catch (Exception) { /* The saved devices may still be starting. Retry briefly. */ }
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _healthTimer.Stop();
        _tray?.Dispose();
        _hotkeys.Dispose();
        _audio.Dispose();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested || _tray is null) return;
        args.Cancel = true;
        HideToTray();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_tray is null || _hiddenInTray || !args.DidPresenterChange) return;
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            HideToTray();
    }

    private void HideToTray()
    {
        if (_hiddenInTray) return;
        _hiddenInTray = true;
        AppWindow.Hide();
    }

    internal void StartInTray()
    {
        if (_tray is null || !_settings.Onboarded) return;
        if (!_audio.IsConfigured)
        {
            _startupAudioRetryUntil = DateTimeOffset.UtcNow.AddMinutes(2);
            _nextStartupAudioAttempt = DateTimeOffset.UtcNow;
        }
        HideToTray();
    }

    private void ShowFromTray()
    {
        _hiddenInTray = false;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }
}
