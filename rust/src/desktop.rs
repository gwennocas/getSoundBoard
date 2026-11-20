use super::{
    bus, play_routed_trimmed, start_voice, Bus, OutputStream, Result, Sink, VoiceControls,
    VoiceSettings,
};
use cpal::{
    traits::{DeviceTrait, HostTrait, StreamTrait},
    Device, FromSample, Sample, SampleFormat, SizedSample,
};
use eframe::egui;
use global_hotkey::{hotkey::HotKey, GlobalHotKeyEvent, GlobalHotKeyManager, HotKeyState};
use rodio::Source;
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeSet, HashMap},
    fs::{self, File},
    io::{BufReader, Write},
    path::{Path, PathBuf},
    sync::{mpsc, Arc, Mutex},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

#[cfg(windows)]
use tray_icon::{
    menu::{Menu, MenuEvent, MenuItem},
    Icon, TrayIcon, TrayIconBuilder, TrayIconEvent,
};

const STOP_ALL: usize = usize::MAX;
const TOGGLE_PAUSE: usize = usize::MAX - 1;
const PREVIOUS: usize = usize::MAX - 2;
const NEXT: usize = usize::MAX - 3;
const RANDOM: usize = usize::MAX - 4;
const INDEX_BASE: usize = usize::MAX - 20;

#[derive(Clone, Serialize, Deserialize)]
struct Clip {
    title: String,
    path: PathBuf,
    key: String,
    #[serde(default)]
    category: String,
    #[serde(default)]
    duration_ms: u64,
    #[serde(default)]
    start_ms: u64,
    #[serde(default)]
    end_ms: u64,
    #[serde(default = "default_clip_gain")]
    gain_percent: f32,
    #[serde(default = "default_normalization_gain")]
    normalization_gain: f32,
}

#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
enum PlayMode {
    Both,
    Speakers,
    Microphone,
}

impl Default for PlayMode {
    fn default() -> Self {
        Self::Both
    }
}

#[derive(Serialize, Deserialize)]
#[serde(default)]
struct Settings {
    speaker_gain: f32,
    chat_gain: f32,
    repeat: bool,
    overlap: bool,
    play_mode: PlayMode,
    index_hotkeys: bool,
    microphone: String,
    speakers: String,
    chat_output: String,
    send_voice_to_chat: bool,
    start_with_windows: bool,
    microphone_gain: f32,
    noise_suppression: bool,
    suppression_strength: f32,
    voice_clarity: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            speaker_gain: 0.35,
            chat_gain: 0.70,
            repeat: false,
            overlap: true,
            play_mode: PlayMode::Both,
            index_hotkeys: false,
            microphone: String::new(),
            speakers: String::new(),
            chat_output: String::new(),
            send_voice_to_chat: false,
            start_with_windows: false,
            microphone_gain: 1.0,
            noise_suppression: false,
            suppression_strength: 0.5,
            voice_clarity: false,
        }
    }
}

struct ActiveClip {
    path: PathBuf,
    sinks: Vec<Sink>,
    gain: f32,
    speaker_slot: Option<usize>,
    chat_slot: Option<usize>,
}

struct AudioRoute {
    _speaker_stream: OutputStream,
    _chat_stream: Option<OutputStream>,
    _microphone_stream: Option<cpal::Stream>,
    speakers: Bus,
    chat: Option<Bus>,
    voice_controls: Option<Arc<VoiceControls>>,
}

struct Recorder {
    stream: Option<cpal::Stream>,
    samples: Arc<Mutex<Vec<f32>>>,
    rate: u32,
    channels: u16,
    started: Instant,
}

fn record_input<T: SizedSample>(
    device: &Device,
    config: &cpal::StreamConfig,
    samples: Arc<Mutex<Vec<f32>>>,
    limit: usize,
) -> Result<cpal::Stream>
where
    f32: FromSample<T>,
{
    Ok(device.build_input_stream(
        config,
        move |data: &[T], _| {
            if let Ok(mut samples) = samples.try_lock() {
                let available = limit.saturating_sub(samples.len());
                samples.extend(
                    data.iter()
                        .take(available)
                        .map(|sample| f32::from_sample(*sample)),
                );
            }
        },
        |error| eprintln!("Gravação interrompida: {error}"),
        None,
    )?)
}

fn start_recording(device: &Device) -> Result<Recorder> {
    let format = device.default_input_config()?;
    let config = format.config();
    let channels = config.channels;
    if !(1..=2).contains(&channels) {
        return Err("A gravação aceita microfones mono ou estéreo".into());
    }
    let rate = config.sample_rate.0;
    let limit = rate as usize * channels as usize * 300;
    let samples = Arc::new(Mutex::new(Vec::with_capacity(limit)));
    let stream = match format.sample_format() {
        SampleFormat::F32 => record_input::<f32>(device, &config, samples.clone(), limit)?,
        SampleFormat::I16 => record_input::<i16>(device, &config, samples.clone(), limit)?,
        SampleFormat::U16 => record_input::<u16>(device, &config, samples.clone(), limit)?,
        SampleFormat::I32 => record_input::<i32>(device, &config, samples.clone(), limit)?,
        other => return Err(format!("Formato de microfone incompatível: {other}").into()),
    };
    stream.play()?;
    Ok(Recorder {
        stream: Some(stream),
        samples,
        rate,
        channels,
        started: Instant::now(),
    })
}

fn file_duration_ms(path: &Path) -> Option<u64> {
    rodio::Decoder::new(BufReader::new(File::open(path).ok()?))
        .ok()?
        .total_duration()
        .map(|duration| duration.as_millis().min(u64::MAX as u128) as u64)
}

fn default_clip_gain() -> f32 {
    100.0
}

fn default_normalization_gain() -> f32 {
    1.0
}

fn peak_normalization_gain(path: &Path) -> Result<f32> {
    let decoder = rodio::Decoder::new(BufReader::new(File::open(path)?))?;
    let peak = decoder
        .convert_samples::<f32>()
        .fold(0.0_f32, |peak, sample| peak.max(sample.abs()));
    if peak <= 0.000_001 {
        return Err("O som está silencioso demais para normalizar".into());
    }
    // shortcut: boost is capped at 60 dB; raise it if usable quiet clips remain low.
    Ok((0.95 / peak).min(1_000.0))
}

fn waveform_peaks(path: &Path) -> Result<Vec<f32>> {
    const BUCKETS: usize = 512;
    let decoder = rodio::Decoder::new(BufReader::new(File::open(path)?))?;
    let duration = decoder
        .total_duration()
        .ok_or("Duração do áudio indisponível")?;
    let sample_count = (duration.as_secs_f64()
        * decoder.sample_rate() as f64
        * f64::from(decoder.channels())) as u128;
    if sample_count == 0 {
        return Err("O arquivo de áudio está vazio".into());
    }
    let mut peaks = vec![0.0_f32; BUCKETS];
    for (index, sample) in decoder.convert_samples::<f32>().enumerate() {
        let bucket =
            ((index as u128 * BUCKETS as u128) / sample_count).min((BUCKETS - 1) as u128) as usize;
        peaks[bucket] = peaks[bucket].max(sample.abs());
    }
    Ok(peaks)
}

fn load(root: &Path) -> Result<Vec<Clip>> {
    let saved = root.join("rust-library.json");
    if saved.try_exists()? {
        let mut clips: Vec<Clip> = serde_json::from_reader(File::open(saved)?)?;
        for clip in &mut clips {
            if clip.duration_ms == 0 {
                clip.duration_ms = file_duration_ms(&clip.path).unwrap_or(0);
            }
        }
        return Ok(clips);
    }
    let legacy = root.join("library.json");
    if !legacy.try_exists()? {
        return Ok(Vec::new());
    }
    #[derive(Deserialize)]
    #[serde(rename_all = "PascalCase")]
    struct Legacy {
        clips: Vec<OldClip>,
    }
    #[derive(Deserialize)]
    #[serde(rename_all = "PascalCase")]
    struct OldClip {
        title: String,
        file_name: String,
        #[serde(default)]
        hotkey: String,
    }
    let old: Legacy = serde_json::from_reader(File::open(legacy)?)?;
    old.clips
        .into_iter()
        .map(|c| {
            let name = Path::new(&c.file_name);
            if name.file_name() != Some(name.as_os_str()) {
                return Err("Nome de arquivo inválido na biblioteca".into());
            }
            let path = root.join("Audio").join(name);
            let duration_ms = file_duration_ms(&path).unwrap_or(0);
            Ok(Clip {
                title: c.title,
                path,
                key: c.hotkey,
                category: String::new(),
                duration_ms,
                start_ms: 0,
                end_ms: 0,
                gain_percent: 100.0,
                normalization_gain: 1.0,
            })
        })
        .collect()
}

fn supported(path: &Path) -> bool {
    path.extension()
        .and_then(|ext| ext.to_str())
        .is_some_and(|ext| {
            matches!(
                ext.to_ascii_lowercase().as_str(),
                "wav" | "mp3" | "ogg" | "flac" | "aiff" | "aif" | "m4a" | "aac" | "mp4"
            )
        })
}

fn audio_files(root: &Path) -> Result<Vec<PathBuf>> {
    let mut stack = vec![root.to_owned()];
    let mut files = Vec::new();
    while let Some(dir) = stack.pop() {
        for entry in fs::read_dir(dir)? {
            let entry = entry?;
            let path = entry.path();
            let kind = entry.file_type()?;
            if kind.is_dir() {
                stack.push(path);
            } else if kind.is_file() && supported(&path) {
                files.push(path);
            }
        }
    }
    files.sort();
    Ok(files)
}

struct Board {
    root: PathBuf,
    _lock: File,
    clips: Vec<Clip>,
    playing: Vec<ActiveClip>,
    inputs: Vec<(String, Device)>,
    outputs: Vec<(String, Device)>,
    route: Option<AudioRoute>,
    settings: Settings,
    keys: Option<GlobalHotKeyManager>,
    active: HashMap<u32, (HotKey, usize)>,
    status: String,
    edit: Option<(usize, Clip)>,
    recording: Option<Recorder>,
    search: String,
    category: String,
    paused: bool,
    selected: Option<PathBuf>,
    waveform: Option<(PathBuf, Vec<f32>)>,
    waveform_loading: Option<PathBuf>,
    waveform_error: Option<(PathBuf, String)>,
    waveform_tx: mpsc::Sender<(PathBuf, std::result::Result<Vec<f32>, String>)>,
    waveform_rx: mpsc::Receiver<(PathBuf, std::result::Result<Vec<f32>, String>)>,
    waveform_drag_handle: Option<usize>,
    #[cfg(windows)]
    _tray: TrayIcon,
    #[cfg(windows)]
    exit_requested: bool,
}

impl Board {
    fn refresh_devices(&mut self) -> Result<()> {
        let host = cpal::default_host();
        let inputs = host
            .input_devices()?
            .filter_map(|device| device.name().ok().map(|name| (name, device)))
            .collect();
        let outputs = host
            .output_devices()?
            .filter_map(|device| device.name().ok().map(|name| (name, device)))
            .collect();
        self.inputs = inputs;
        self.outputs = outputs;
        self.status = "Lista de dispositivos atualizada; reconecte o áudio se necessário".into();
        Ok(())
    }

    fn save(&mut self, clips: Vec<Clip>) -> Result<()> {
        let mut tmp = tempfile::NamedTempFile::new_in(&self.root)?;
        serde_json::to_writer_pretty(&mut tmp, &clips)?;
        tmp.flush()?;
        tmp.as_file().sync_all()?;
        tmp.persist(self.root.join("rust-library.json"))?;
        self.clips = clips;
        if self
            .selected
            .as_ref()
            .is_some_and(|path| !self.clips.iter().any(|clip| &clip.path == path))
        {
            self.selected = None;
        }
        self.bind();
        Ok(())
    }

    fn save_settings(&mut self) -> Result<()> {
        let mut tmp = tempfile::NamedTempFile::new_in(&self.root)?;
        serde_json::to_writer_pretty(&mut tmp, &self.settings)?;
        tmp.flush()?;
        tmp.as_file().sync_all()?;
        tmp.persist(self.root.join("rust-settings.json"))?;
        Ok(())
    }

    fn update_voice_controls(&self) {
        if let Some(controls) = self
            .route
            .as_ref()
            .and_then(|route| route.voice_controls.as_ref())
        {
            controls.set(VoiceSettings {
                microphone_gain: self.settings.microphone_gain.clamp(0.0, 3.0),
                noise_suppression: self.settings.noise_suppression,
                suppression_strength: self.settings.suppression_strength.clamp(0.0, 1.0),
                clarity: self.settings.voice_clarity,
            });
        }
    }

    fn load_waveform(&mut self, path: &Path) -> Result<()> {
        let path = path.to_owned();
        let sender = self.waveform_tx.clone();
        self.waveform_loading = Some(path.clone());
        self.waveform_error = None;
        std::thread::Builder::new()
            .name("getsoundboard-waveform".into())
            .spawn(move || {
                let result = waveform_peaks(&path).map_err(|error| error.to_string());
                let _ = sender.send((path, result));
            })?;
        Ok(())
    }

    fn bind(&mut self) {
        let Some(manager) = &self.keys else { return };
        self.status.clear();
        for (_, (key, _)) in self.active.drain() {
            let _ = manager.unregister(key);
        }
        for (label, action) in [
            ("Ctrl+Alt+Backspace", STOP_ALL),
            ("Ctrl+Alt+P", TOGGLE_PAUSE),
            ("Ctrl+Alt+Left", PREVIOUS),
            ("Ctrl+Alt+Right", NEXT),
            ("Ctrl+Alt+R", RANDOM),
        ] {
            match label.parse::<HotKey>() {
                Ok(key) if manager.register(key).is_ok() => {
                    self.active.insert(key.id(), (key, action));
                }
                _ => self.status = format!("Atalho de controle indisponível: {label}"),
            }
        }
        if self.settings.index_hotkeys {
            for i in 0..10 {
                let label = format!("Ctrl+Alt+{}", if i == 9 { 0 } else { i + 1 });
                match label.parse::<HotKey>() {
                    Ok(key)
                        if !self.active.contains_key(&key.id())
                            && manager.register(key).is_ok() =>
                    {
                        self.active.insert(key.id(), (key, INDEX_BASE + i));
                    }
                    _ => self.status = format!("Atalho por índice indisponível: {label}"),
                }
            }
        }
        for (i, clip) in self.clips.iter().enumerate() {
            if clip.key.is_empty() {
                continue;
            }
            let Ok(key) = clip.key.parse::<HotKey>() else {
                self.status = format!("Atalho inválido em {}", clip.title);
                continue;
            };
            if self.active.contains_key(&key.id()) || manager.register(key).is_err() {
                self.status = format!("Atalho indisponível: {}", clip.key);
            } else {
                self.active.insert(key.id(), (key, i));
            }
        }
    }

    fn stop_all(&mut self) {
        for active in &self.playing {
            for sink in &active.sinks {
                sink.stop();
            }
        }
        self.playing.clear();
        self.paused = false;
    }

    fn stop_clip(&mut self, path: &Path) {
        for active in self.playing.iter().filter(|active| active.path == path) {
            for sink in &active.sinks {
                sink.stop();
            }
        }
        self.playing.retain(|active| active.path != path);
    }

    fn toggle_pause(&mut self) {
        self.paused = !self.paused;
        for active in &self.playing {
            for sink in &active.sinks {
                if self.paused {
                    sink.pause();
                } else {
                    sink.play();
                }
            }
        }
    }

    fn trigger(&mut self, i: usize) -> Result<()> {
        let clip = self.clips.get(i).ok_or("Som não encontrado")?;
        let (path, start_ms, end_ms, duration_ms, gain_percent, normalization_gain) = (
            clip.path.clone(),
            clip.start_ms,
            clip.end_ms,
            clip.duration_ms,
            clip.gain_percent,
            clip.normalization_gain,
        );
        if (end_ms != 0 && end_ms <= start_ms)
            || (duration_ms != 0 && (start_ms >= duration_ms || end_ms > duration_ms))
        {
            return Err("O trecho de reprodução é inválido; revise o corte em Editar".into());
        }
        let mode = self.settings.play_mode;
        let gain = gain_percent.clamp(0.0, 200.0) / 100.0 * normalization_gain.clamp(0.0, 1_000.0);
        let gains = (
            self.settings.speaker_gain * gain,
            self.settings.chat_gain * gain,
        );
        let repeat = self.settings.repeat;
        if self.route.is_none() {
            return Err("Conecte os dispositivos de áudio primeiro".into());
        }
        if mode == PlayMode::Microphone
            && self
                .route
                .as_ref()
                .is_some_and(|route| route.chat.is_none())
        {
            return Err("Conecte o microfone e a saída do chat primeiro".into());
        }
        if !self.settings.overlap {
            self.stop_all();
        }
        let route = self
            .route
            .as_ref()
            .ok_or("Conecte os dispositivos de áudio primeiro")?;
        let (monitor, chat) = match mode {
            PlayMode::Both => (Some(&route.speakers), route.chat.as_ref()),
            PlayMode::Speakers => (Some(&route.speakers), None),
            PlayMode::Microphone => (None, route.chat.as_ref()),
        };
        if monitor.is_none() && chat.is_none() {
            return Err("Conecte os dispositivos de áudio primeiro".into());
        }
        let path_str = path.to_str().ok_or("Caminho do áudio não representável")?;
        let sinks = play_routed_trimmed(path_str, monitor, chat, gains, repeat, start_ms, end_ms)?;
        let speaker_slot = monitor.map(|_| 0);
        let chat_slot = chat.map(|_| usize::from(monitor.is_some()));
        if self.paused {
            for sink in &sinks {
                sink.pause();
            }
        }
        self.playing.push(ActiveClip {
            path: path.clone(),
            sinks,
            gain,
            speaker_slot,
            chat_slot,
        });
        self.selected = Some(path);
        Ok(())
    }

    fn play_relative(&mut self, next: bool) -> Result<()> {
        if self.clips.is_empty() {
            return Err("A biblioteca está vazia".into());
        }
        let current = self
            .selected
            .as_ref()
            .and_then(|path| self.clips.iter().position(|clip| &clip.path == path));
        let index = match (current, next) {
            (Some(i), true) => (i + 1) % self.clips.len(),
            (Some(i), false) => (i + self.clips.len() - 1) % self.clips.len(),
            (None, true) => 0,
            (None, false) => self.clips.len() - 1,
        };
        self.trigger(index)
    }

    fn play_random(&mut self) -> Result<()> {
        if self.clips.is_empty() {
            return Err("A biblioteca está vazia".into());
        }
        let seed = SystemTime::now().duration_since(UNIX_EPOCH)?.subsec_nanos() as usize;
        self.trigger(seed % self.clips.len())
    }

    fn import(&mut self, paths: Vec<PathBuf>) -> Result<()> {
        let mut clips = self.clips.clone();
        let dir = self.root.join("RustAudio");
        fs::create_dir_all(&dir)?;
        let mut staged = Vec::new();
        for p in paths.into_iter().filter(|path| supported(path)) {
            let mut source = File::open(&p)?;
            let mut tmp = tempfile::Builder::new()
                .suffix(&format!(
                    ".{}",
                    p.extension().and_then(|x| x.to_str()).unwrap_or("audio")
                ))
                .tempfile_in(&dir)?;
            std::io::copy(&mut source, &mut tmp)?;
            let duration_ms = rodio::Decoder::new(BufReader::new(File::open(tmp.path())?))?
                .total_duration()
                .map(|duration| duration.as_millis().min(u64::MAX as u128) as u64)
                .unwrap_or(0);
            clips.push(Clip {
                title: p.file_stem().unwrap_or_default().to_string_lossy().into(),
                path: tmp.path().into(),
                key: String::new(),
                category: String::new(),
                duration_ms,
                start_ms: 0,
                end_ms: 0,
                gain_percent: 100.0,
                normalization_gain: 1.0,
            });
            staged.push(tmp.into_temp_path());
        }
        if staged.is_empty() {
            return Err("Nenhum arquivo de áudio compatível foi encontrado".into());
        }
        let mut copied = Vec::new();
        let saved = (|| -> Result<()> {
            for p in staged {
                copied.push(p.keep()?);
            }
            self.save(clips)
        })();
        if let Err(e) = saved {
            for p in copied {
                let _ = fs::remove_file(p);
            }
            return Err(e);
        }
        Ok(())
    }

    fn import_folder(&mut self, folder: &Path) -> Result<()> {
        self.import(audio_files(folder)?)
    }

    fn set_volume(&mut self, speaker: bool, gain: f32) {
        for active in &self.playing {
            let slot = if speaker {
                active.speaker_slot
            } else {
                active.chat_slot
            };
            if let Some(sink) = slot.and_then(|slot| active.sinks.get(slot)) {
                sink.set_volume(gain * active.gain);
            }
        }
    }

    fn set_clip_gain(&mut self, path: &Path, gain: f32) {
        let speaker_gain = self.settings.speaker_gain;
        let chat_gain = self.settings.chat_gain;
        for active in self.playing.iter_mut().filter(|active| active.path == path) {
            active.gain = gain;
            if let Some(sink) = active.speaker_slot.and_then(|slot| active.sinks.get(slot)) {
                sink.set_volume(speaker_gain * gain);
            }
            if let Some(sink) = active.chat_slot.and_then(|slot| active.sinks.get(slot)) {
                sink.set_volume(chat_gain * gain);
            }
        }
    }

    fn connect_audio(&mut self) -> Result<()> {
        let speaker_device = self
            .outputs
            .iter()
            .find(|(name, _)| name == &self.settings.speakers)
            .map(|(_, device)| device)
            .ok_or("Escolha uma saída para o fone")?;
        let (speaker_stream, speaker_output) = OutputStream::try_from_device(speaker_device)?;
        let (speakers, speaker_mix) = bus();
        speaker_output.play_raw(speaker_mix)?;

        let (chat_stream, chat, microphone_stream, voice_controls) =
            if self.settings.send_voice_to_chat {
                let mic = self
                    .inputs
                    .iter()
                    .find(|(name, _)| name == &self.settings.microphone)
                    .map(|(_, device)| device)
                    .ok_or("Escolha seu microfone")?;
                if self.settings.chat_output.is_empty()
                    || self.settings.chat_output == self.settings.speakers
                {
                    return Err("Escolha uma saída de chat diferente da saída do fone".into());
                }
                let chat_device = self
                    .outputs
                    .iter()
                    .find(|(name, _)| name == &self.settings.chat_output)
                    .map(|(_, device)| device)
                    .ok_or("Escolha a saída virtual usada como microfone no chat")?;
                let (stream, output) = OutputStream::try_from_device(chat_device)?;
                let (chat, mix) = bus();
                output.play_raw(mix)?;
                let (capture, controls) = start_voice(
                    mic,
                    &chat,
                    VoiceSettings {
                        microphone_gain: self.settings.microphone_gain,
                        noise_suppression: self.settings.noise_suppression,
                        suppression_strength: self.settings.suppression_strength,
                        clarity: self.settings.voice_clarity,
                    },
                )?;
                (Some(stream), Some(chat), Some(capture), Some(controls))
            } else {
                (None, None, None, None)
            };

        self.stop_all();
        self.route = Some(AudioRoute {
            _speaker_stream: speaker_stream,
            _chat_stream: chat_stream,
            _microphone_stream: microphone_stream,
            speakers,
            chat,
            voice_controls,
        });
        if self
            .route
            .as_ref()
            .is_some_and(|route| route.chat.is_none())
        {
            self.settings.play_mode = PlayMode::Speakers;
        }
        self.save_settings()?;
        self.status = if self.settings.send_voice_to_chat {
            "Áudio conectado: voz e sons seguem para o chat".into()
        } else {
            "Áudio conectado: sons saem pelo fone".into()
        };
        Ok(())
    }

    fn start_recording(&mut self) -> Result<()> {
        if self.recording.is_some() {
            return Err("Uma gravação já está em andamento".into());
        }
        let device = self
            .inputs
            .iter()
            .find(|(name, _)| name == &self.settings.microphone)
            .map(|(_, device)| device)
            .ok_or("Escolha o microfone antes de gravar")?;
        self.recording = Some(start_recording(device)?);
        self.status = "Gravando o microfone (limite de 5 minutos)…".into();
        Ok(())
    }

    fn finish_recording(&mut self) -> Result<()> {
        let recorder = self
            .recording
            .as_mut()
            .ok_or("Nenhuma gravação em andamento")?;
        drop(recorder.stream.take());
        let rate = recorder.rate;
        let channels = recorder.channels;
        let now = SystemTime::now().duration_since(UNIX_EPOCH)?;
        let timestamp = now.as_secs();
        let dir = self.root.join("RustAudio");
        fs::create_dir_all(&dir)?;
        let target = dir.join(format!("recording-{}.wav", now.as_nanos()));
        let tmp = tempfile::Builder::new().suffix(".wav").tempfile_in(&dir)?;
        let duration_ms;
        {
            let samples = recorder
                .samples
                .lock()
                .map_err(|_| "A gravação não pôde ser acessada")?;
            if samples.is_empty() {
                return Err("A gravação ficou vazia".into());
            }
            duration_ms = (samples.len() as u128 * 1000 / (rate as u128 * channels as u128))
                .min(u64::MAX as u128) as u64;
            let spec = hound::WavSpec {
                channels,
                sample_rate: rate,
                bits_per_sample: 32,
                sample_format: hound::SampleFormat::Float,
            };
            let mut writer = hound::WavWriter::new(tmp.reopen()?, spec)?;
            for sample in samples.iter().copied() {
                writer.write_sample(sample.clamp(-1.0, 1.0))?;
            }
            writer.finalize()?;
        }
        tmp.persist(&target)?;
        let title = format!("Gravação {timestamp}");
        let mut clips = self.clips.clone();
        clips.push(Clip {
            title,
            path: target.clone(),
            key: String::new(),
            category: String::new(),
            duration_ms,
            start_ms: 0,
            end_ms: 0,
            gain_percent: 100.0,
            normalization_gain: 1.0,
        });
        if let Err(error) = self.save(clips) {
            let _ = fs::remove_file(target);
            return Err(error);
        }
        self.recording = None;
        self.status = "Gravação salva na biblioteca".into();
        Ok(())
    }
}

impl Drop for Board {
    fn drop(&mut self) {
        if self.recording.is_some() {
            if let Err(error) = self.finish_recording() {
                eprintln!("Gravação não salva ao fechar: {error}");
            }
        }
    }
}

impl eframe::App for Board {
    fn update(&mut self, ctx: &egui::Context, _: &mut eframe::Frame) {
        ctx.request_repaint_after(Duration::from_millis(60));
        while let Ok((path, result)) = self.waveform_rx.try_recv() {
            if self.waveform_loading.as_ref() == Some(&path) {
                self.waveform_loading = None;
                match result {
                    Ok(peaks) => self.waveform = Some((path, peaks)),
                    Err(error) => self.waveform_error = Some((path, error)),
                }
            }
        }
        #[cfg(windows)]
        {
            while let Ok(event) = TrayIconEvent::receiver().try_recv() {
                if matches!(
                    event,
                    TrayIconEvent::Click {
                        button: tray_icon::MouseButton::Left,
                        ..
                    } | TrayIconEvent::DoubleClick {
                        button: tray_icon::MouseButton::Left,
                        ..
                    }
                ) {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
                    ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
                }
            }
            while let Ok(event) = MenuEvent::receiver().try_recv() {
                match event.id().as_ref() {
                    "open" => {
                        ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
                        ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
                    }
                    "stop" => self.stop_all(),
                    "exit" => {
                        self.exit_requested = true;
                        ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                    }
                    _ => {}
                }
            }
            if ctx.input(|input| input.viewport().close_requested()) && !self.exit_requested {
                ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
                ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
            }
        }
        self.playing
            .retain(|active| active.sinks.iter().any(|sink| !sink.empty()));
        if self.recording.as_ref().is_some_and(|recorder| {
            recorder.stream.is_some() && recorder.started.elapsed() >= Duration::from_secs(300)
        }) {
            if let Err(error) = self.finish_recording() {
                self.status = format!("Gravação não salva: {error}");
            }
        }
        while let Ok(ev) = GlobalHotKeyEvent::receiver().try_recv() {
            if ev.state == HotKeyState::Pressed {
                if let Some(&(_, action)) = self.active.get(&ev.id) {
                    if action == STOP_ALL {
                        self.stop_all();
                    } else if action == TOGGLE_PAUSE {
                        self.toggle_pause();
                    } else if action == PREVIOUS {
                        if let Err(e) = self.play_relative(false) {
                            self.status = e.to_string();
                        }
                    } else if action == NEXT {
                        if let Err(e) = self.play_relative(true) {
                            self.status = e.to_string();
                        }
                    } else if action == RANDOM {
                        if let Err(e) = self.play_random() {
                            self.status = e.to_string();
                        }
                    } else if (INDEX_BASE..INDEX_BASE + 10).contains(&action) {
                        if let Err(e) = self.trigger(action - INDEX_BASE) {
                            self.status = e.to_string();
                        }
                    } else if let Err(e) = self.trigger(action) {
                        self.status = e.to_string();
                    }
                }
            }
        }

        let dropped = ctx.input(|input| {
            input
                .raw
                .dropped_files
                .iter()
                .filter_map(|file| file.path.clone())
                .collect::<Vec<_>>()
        });
        if !dropped.is_empty() {
            if let Err(e) = self.import(dropped) {
                self.status = e.to_string();
            }
        }

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.heading("getSoundBoard");
            let mut connect_clicked = false;
            ui.group(|ui| {
                ui.label("Áudio");
                ui.horizontal(|ui| {
                    egui::ComboBox::from_id_salt("speaker_device")
                        .selected_text(if self.settings.speakers.is_empty() { "Escolha o fone" } else { &self.settings.speakers })
                        .show_ui(ui, |ui| {
                            for (name, _) in &self.outputs {
                                ui.selectable_value(&mut self.settings.speakers, name.clone(), name);
                            }
                        });
                    ui.checkbox(&mut self.settings.send_voice_to_chat, "Enviar voz ao chat");
                });
                #[cfg(windows)]
                {
                    let mut start_with_windows = self.settings.start_with_windows;
                    if ui
                        .checkbox(&mut start_with_windows, "Iniciar junto com o Windows")
                        .changed()
                    {
                        match sync_autostart(start_with_windows) {
                            Ok(_) => {
                                self.settings.start_with_windows = start_with_windows;
                                if let Err(error) = self.save_settings() {
                                    self.settings.start_with_windows = !start_with_windows;
                                    let _ = sync_autostart(self.settings.start_with_windows);
                                    self.status = error.to_string();
                                }
                            }
                            Err(error) => self.status = format!("Inicialização automática: {error}"),
                        }
                    }
                }
                if self.settings.send_voice_to_chat {
                    ui.horizontal(|ui| {
                        egui::ComboBox::from_id_salt("microphone_device")
                            .selected_text(if self.settings.microphone.is_empty() { "Escolha o microfone" } else { &self.settings.microphone })
                            .show_ui(ui, |ui| {
                                for (name, _) in &self.inputs {
                                    ui.selectable_value(&mut self.settings.microphone, name.clone(), name);
                                }
                            });
                        egui::ComboBox::from_id_salt("chat_device")
                            .selected_text(if self.settings.chat_output.is_empty() { "Escolha a saída virtual do chat" } else { &self.settings.chat_output })
                            .show_ui(ui, |ui| {
                                for (name, _) in &self.outputs {
                                    if name != &self.settings.speakers {
                                        ui.selectable_value(&mut self.settings.chat_output, name.clone(), name);
                                    }
                                }
                            });
                    });
                    ui.label("Processamento da voz");
                    let mut voice_changed = ui
                        .add(
                            egui::Slider::new(&mut self.settings.microphone_gain, 0.0..=3.0)
                                .text("Ganho do microfone"),
                        )
                        .changed();
                    voice_changed |= ui
                        .checkbox(&mut self.settings.noise_suppression, "Supressão RNNoise")
                        .changed();
                    if self.settings.noise_suppression {
                        voice_changed |= ui
                            .add(
                                egui::Slider::new(
                                    &mut self.settings.suppression_strength,
                                    0.0..=1.0,
                                )
                                .text("Intensidade da supressão"),
                            )
                            .changed();
                    }
                    voice_changed |= ui
                        .checkbox(&mut self.settings.voice_clarity, "Clareza da voz")
                        .changed();
                    ui.weak("Filtros afetam só a voz do microfone; os sons da biblioteca permanecem intactos.");
                    if voice_changed {
                        self.update_voice_controls();
                        if let Err(error) = self.save_settings() {
                            self.status = error.to_string();
                        }
                    }
                }
                if ui.button("Conectar áudio").clicked() {
                    connect_clicked = true;
                }
                if ui.button("Atualizar dispositivos").clicked() {
                    if let Err(error) = self.refresh_devices() {
                        self.status = format!("Não foi possível atualizar dispositivos: {error}");
                    }
                }
                if self.route.is_none() {
                    ui.weak("Escolha uma saída e conecte. Para voz no chat, selecione o microfone e uma entrada virtual (por exemplo, VB-CABLE).");
                }
            });
            if connect_clicked {
                if let Err(e) = self.connect_audio() {
                    self.status = format!("Não foi possível conectar: {e}");
                }
            }
            ui.horizontal(|ui| {
                if self
                    .recording
                    .as_ref()
                    .is_some_and(|recorder| recorder.stream.is_some())
                {
                    let elapsed = self
                        .recording
                        .as_ref()
                        .map(|recorder| recorder.started.elapsed().as_secs())
                        .unwrap_or_default();
                    ui.label(format!("Gravando {:02}:{:02}", elapsed / 60, elapsed % 60));
                    if ui.button("■ Parar e salvar gravação").clicked() {
                        if let Err(error) = self.finish_recording() {
                            self.status = format!("Gravação não salva: {error}");
                        }
                    }
                } else if self.recording.is_some() {
                    ui.label("Gravação parada; aguardando salvamento");
                    if ui.button("Salvar gravação").clicked() {
                        if let Err(error) = self.finish_recording() {
                            self.status = format!("Gravação não salva: {error}");
                        }
                    }
                } else if ui.button("● Gravar microfone").clicked() {
                    if let Err(error) = self.start_recording() {
                        self.status = error.to_string();
                    }
                }
            });
            ui.horizontal(|ui| {
                if ui.button("Adicionar sons…").clicked() {
                    if let Some(paths) = egui_file_dialog() {
                        if let Err(e) = self.import(paths) {
                            self.status = e.to_string();
                        }
                    }
                }
                if ui.button("Importar pasta…").clicked() {
                    if let Some(path) = rfd::FileDialog::new().pick_folder() {
                        if let Err(e) = self.import_folder(&path) {
                            self.status = e.to_string();
                        }
                    }
                }
                if ui.button("⏹ Parar tudo").clicked() {
                    self.stop_all();
                }
                if ui.button(if self.paused { "▶ Retomar" } else { "⏸ Pausar" }).clicked() {
                    self.toggle_pause();
                }
                if ui.button("⏮ Anterior").clicked() {
                    if let Err(e) = self.play_relative(false) { self.status = e.to_string(); }
                }
                if ui.button("⏭ Próximo").clicked() {
                    if let Err(e) = self.play_relative(true) { self.status = e.to_string(); }
                }
                if ui.button("🎲 Aleatório").clicked() {
                    if let Err(e) = self.play_random() { self.status = e.to_string(); }
                }
                if ui.checkbox(&mut self.settings.repeat, "Repetir").changed() {
                    if let Err(e) = self.save_settings() {
                        self.status = e.to_string();
                    }
                }
                if ui.checkbox(&mut self.settings.overlap, "Sobrepor sons").changed() {
                    if let Err(e) = self.save_settings() {
                        self.status = e.to_string();
                    }
                    if !self.settings.overlap {
                        self.stop_all();
                    }
                }
                if ui
                    .checkbox(
                        &mut self.settings.index_hotkeys,
                        "Ctrl+Alt+1–0: tocar por posição",
                    )
                    .changed()
                {
                    if let Err(e) = self.save_settings() {
                        self.status = e.to_string();
                    } else {
                        self.bind();
                    }
                }
            });

            let mut mode_changed = false;
            ui.horizontal(|ui| {
                egui::ComboBox::from_id_salt("play_mode")
                    .selected_text(match self.settings.play_mode {
                        PlayMode::Both => "Fone + microfone",
                        PlayMode::Speakers => "Somente fone",
                        PlayMode::Microphone => "Somente microfone",
                    })
                    .show_ui(ui, |ui| {
                        mode_changed |= ui.selectable_value(&mut self.settings.play_mode, PlayMode::Both, "Fone + microfone").changed();
                        mode_changed |= ui.selectable_value(&mut self.settings.play_mode, PlayMode::Speakers, "Somente fone").changed();
                        if self.route.as_ref().is_some_and(|route| route.chat.is_some()) {
                            mode_changed |= ui.selectable_value(&mut self.settings.play_mode, PlayMode::Microphone, "Somente microfone").changed();
                        }
                    });
                if ui.text_edit_singleline(&mut self.search).changed() {
                    ctx.request_repaint();
                }
                ui.label("Buscar sons");
                let categories: Vec<_> = self
                    .clips
                    .iter()
                    .map(|clip| clip.category.trim())
                    .filter(|name| !name.is_empty())
                    .collect::<BTreeSet<_>>()
                    .into_iter()
                    .collect();
                egui::ComboBox::from_id_salt("category_filter")
                    .selected_text(if self.category.is_empty() { "Todas as categorias" } else { &self.category })
                    .show_ui(ui, |ui| {
                        ui.selectable_value(&mut self.category, String::new(), "Todas as categorias");
                        for category in categories {
                            ui.selectable_value(&mut self.category, category.to_owned(), category);
                        }
                    });
            });
            if mode_changed {
                if let Err(e) = self.save_settings() {
                    self.status = e.to_string();
                }
            }

            let mut changed = ui
                .add(egui::Slider::new(&mut self.settings.speaker_gain, 0.0..=1.0).text("Volume no fone"))
                .changed();
            if self.route.as_ref().is_some_and(|route| route.chat.is_some()) {
                changed |= ui
                    .add(egui::Slider::new(&mut self.settings.chat_gain, 0.0..=1.0).text("Volume no chat"))
                    .changed();
            }
            if changed {
                self.set_volume(true, self.settings.speaker_gain);
                self.set_volume(false, self.settings.chat_gain);
                if let Err(e) = self.save_settings() {
                    self.status = e.to_string();
                }
            }
            if self.route.is_none() && self.settings.play_mode == PlayMode::Microphone {
                ui.colored_label(egui::Color32::YELLOW, "Conecte uma saída de chat para tocar no microfone.");
            }
            ui.weak("Arraste arquivos de áudio para importar. Ctrl+Alt+Backspace para parar; Ctrl+Alt+P para pausar/retomar.");
            if !self.status.is_empty() {
                ui.label(&self.status);
            }
            ui.separator();
            egui::ScrollArea::vertical().show(ui, |ui| {
                let query = self.search.trim().to_lowercase();
                let visible: Vec<_> = self
                    .clips
                    .iter()
                    .enumerate()
                    .filter(|(_, clip)| {
                        (self.category.is_empty() || clip.category == self.category)
                            && (query.is_empty()
                                || clip.title.to_lowercase().contains(&query)
                                || clip.category.to_lowercase().contains(&query))
                    })
                    .map(|(i, _)| i)
                    .collect();
                for i in visible {
                    let clip = &self.clips[i];
                    let path = clip.path.clone();
                    let mut action = 0;
                    ui.horizontal(|ui| {
                        if ui.button("▶ Tocar").clicked() { action = 1; }
                        if ui.button("⏹ Parar").clicked() { action = 2; }
                        if ui.selectable_label(self.selected.as_ref() == Some(&path), &clip.title).clicked() {
                            action = 4;
                        }
                        if !clip.category.is_empty() { ui.weak(format!("[{}]", clip.category)); }
                        if !clip.key.is_empty() { ui.weak(&clip.key); }
                        if ui.button("Editar").clicked() { action = 3; }
                    });
                    match action {
                        1 => if let Err(e) = self.trigger(i) { self.status = e.to_string(); },
                        2 => self.stop_clip(&path),
                        3 => self.edit = Some((i, self.clips[i].clone())),
                        4 => self.selected = Some(path),
                        _ => {}
                    }
                }
            });
        });

        if let Some((i, mut clip)) = self.edit.take() {
            if self
                .waveform
                .as_ref()
                .is_none_or(|(path, _)| path != &clip.path)
                && self.waveform_loading.as_ref() != Some(&clip.path)
                && self
                    .waveform_error
                    .as_ref()
                    .is_none_or(|(path, _)| path != &clip.path)
            {
                if let Err(error) = self.load_waveform(&clip.path) {
                    self.waveform_error = Some((clip.path.clone(), error.to_string()));
                }
            }
            let mut open = true;
            let mut action = 0;
            egui::Window::new("Editar som")
                .collapsible(false)
                .show(ctx, |ui| {
                    ui.label("Nome");
                    ui.text_edit_singleline(&mut clip.title);
                    ui.label("Categoria");
                    ui.text_edit_singleline(&mut clip.category);
                    ui.label("Atalho global (ex. Ctrl+Alt+F1)");
                    ui.text_edit_singleline(&mut clip.key);
                    ui.add(
                        egui::Slider::new(&mut clip.gain_percent, 0.0..=200.0)
                            .text("Volume deste som (%)"),
                    );
                    ui.label(if clip.normalization_gain == 1.0 {
                        "Normalização: desligada"
                    } else {
                        "Normalização: ativa (ganho máximo de 60 dB)"
                    });
                    ui.horizontal(|ui| {
                        if ui.button("Normalizar pico").clicked() {
                            action = 4;
                        }
                        if clip.normalization_gain != 1.0
                            && ui.button("Desfazer normalização").clicked()
                        {
                            action = 5;
                        }
                    });
                    ui.separator();
                    if clip.duration_ms > 0 {
                        ui.label(format!(
                            "Duração: {:.2} s",
                            clip.duration_ms as f64 / 1000.0
                        ));
                        if let Some((_, peaks)) = self
                            .waveform
                            .as_ref()
                            .filter(|(path, _)| path == &clip.path)
                        {
                            waveform_editor(ui, peaks, &mut clip, &mut self.waveform_drag_handle);
                        } else if self.waveform_loading.as_ref() == Some(&clip.path) {
                            ui.horizontal(|ui| {
                                ui.spinner();
                                ui.label("Carregando forma de onda…");
                            });
                        } else if let Some(error) = self
                            .waveform_error
                            .as_ref()
                            .filter(|(path, _)| path == &clip.path)
                            .map(|(_, error)| error.clone())
                        {
                            ui.weak(format!("Forma de onda indisponível: {error}"));
                            if ui.button("Tentar novamente").clicked() {
                                let path = clip.path.clone();
                                self.waveform_error = None;
                                if let Err(error) = self.load_waveform(&path) {
                                    self.waveform_error = Some((path, error.to_string()));
                                }
                            }
                        }
                        ui.add(
                            egui::DragValue::new(&mut clip.start_ms)
                                .range(0..=clip.duration_ms.saturating_sub(1))
                                .speed(100)
                                .suffix(" ms início"),
                        );
                        ui.add(
                            egui::DragValue::new(&mut clip.end_ms)
                                .range(0..=clip.duration_ms)
                                .speed(100)
                                .suffix(" ms fim (0 = final)"),
                        );
                    } else {
                        ui.weak("Duração indisponível; não é possível definir um corte.");
                    }
                    ui.horizontal(|ui| {
                        if ui.button("Salvar").clicked() {
                            action = 1;
                        }
                        if ui.button("Remover da lista").clicked() {
                            action = 2;
                        }
                        if ui.button("Cancelar").clicked() {
                            action = 3;
                        }
                    });
                });
            match action {
                1 => {
                    let mut rows = self.clips.clone();
                    rows[i] = clip.clone();
                    if clip.title.trim().is_empty() {
                        self.status = "Digite um nome para o som".into();
                    } else if !clip.key.is_empty() && clip.key.parse::<HotKey>().is_err() {
                        self.status = "Atalho inválido".into();
                    } else if !clip.gain_percent.is_finite()
                        || !(0.0..=200.0).contains(&clip.gain_percent)
                    {
                        self.status = "O volume do som precisa estar entre 0 e 200%".into();
                    } else if !clip.normalization_gain.is_finite()
                        || !(0.0..=1_000.0).contains(&clip.normalization_gain)
                    {
                        self.status = "O ganho de normalização é inválido".into();
                    } else if (clip.end_ms != 0 && clip.end_ms <= clip.start_ms)
                        || (clip.duration_ms != 0
                            && (clip.start_ms >= clip.duration_ms
                                || clip.end_ms > clip.duration_ms))
                    {
                        self.status = "O fim do corte precisa ficar depois do início".into();
                    } else if rows.iter().enumerate().any(|(j, row)| {
                        j != i && row.key.eq_ignore_ascii_case(&clip.key) && !clip.key.is_empty()
                    }) {
                        self.status = "Esse atalho já pertence a outro som".into();
                    } else {
                        let path = clip.path.clone();
                        let gain = clip.gain_percent / 100.0 * clip.normalization_gain;
                        match self.save(rows) {
                            Ok(()) => {
                                self.set_clip_gain(&path, gain);
                                open = false;
                            }
                            Err(e) => self.status = e.to_string(),
                        }
                    }
                }
                2 => {
                    let mut rows = self.clips.clone();
                    rows.remove(i);
                    match self.save(rows) {
                        Ok(()) => open = false,
                        Err(e) => self.status = e.to_string(),
                    }
                }
                3 => open = false,
                4 => match peak_normalization_gain(&clip.path) {
                    Ok(gain) => {
                        clip.normalization_gain = gain;
                        self.status = if gain >= 1_000.0 {
                            "Normalização aplicada (ganho limitado a 60 dB); salve para manter"
                                .into()
                        } else {
                            "Pico ajustado para −0,4 dB; salve para manter".into()
                        };
                    }
                    Err(error) => self.status = error.to_string(),
                },
                5 => {
                    clip.normalization_gain = 1.0;
                    self.status = "Normalização removida; salve para manter".into();
                }
                _ => {}
            }
            if open {
                self.edit = Some((i, clip));
            }
        }
    }
}

fn app_icon() -> egui::IconData {
    let mut rgba = vec![0; 16 * 16 * 4];
    for y in 0..16 {
        for x in 0..16 {
            let pixel = (y * 16 + x) * 4;
            rgba[pixel..pixel + 4].copy_from_slice(&[35, 91, 180, 255]);
        }
    }
    for (x, height) in [3, 6, 10, 7, 4, 8, 11, 5, 3].into_iter().enumerate() {
        for y in (8 - height / 2)..(8 + height / 2) {
            let pixel = (y * 16 + x + 4) * 4;
            rgba[pixel..pixel + 4].copy_from_slice(&[255, 255, 255, 255]);
        }
    }
    egui::IconData {
        rgba,
        width: 16,
        height: 16,
    }
}

fn egui_file_dialog() -> Option<Vec<PathBuf>> {
    rfd::FileDialog::new()
        .add_filter(
            "Áudio",
            &[
                "wav", "mp3", "ogg", "flac", "aiff", "aif", "m4a", "aac", "mp4",
            ],
        )
        .pick_files()
}

fn waveform_editor(
    ui: &mut egui::Ui,
    peaks: &[f32],
    clip: &mut Clip,
    drag_handle: &mut Option<usize>,
) {
    let duration = clip.duration_ms.max(1);
    let (rect, response) = ui.allocate_exact_size(
        egui::vec2(ui.available_width(), 100.0),
        egui::Sense::click_and_drag(),
    );
    let painter = ui.painter_at(rect);
    painter.rect_filled(rect, 3.0, egui::Color32::from_gray(24));

    let start = clip.start_ms.min(duration - 1) as f32 / duration as f32;
    let end = if clip.end_ms == 0 {
        1.0
    } else {
        clip.end_ms.min(duration) as f32 / duration as f32
    };
    let start_x = rect.left() + rect.width() * start;
    let end_x = rect.left() + rect.width() * end;
    painter.rect_filled(
        egui::Rect::from_min_max(
            egui::pos2(start_x.min(end_x), rect.top()),
            egui::pos2(start_x.max(end_x), rect.bottom()),
        ),
        0.0,
        egui::Color32::from_rgba_unmultiplied(41, 105, 196, 45),
    );

    let center = rect.center().y;
    let half_height = rect.height() * 0.42;
    for (index, peak) in peaks.iter().enumerate() {
        let x = rect.left() + (index as f32 + 0.5) * rect.width() / peaks.len() as f32;
        let amplitude = peak.clamp(0.0, 1.0) * half_height;
        painter.line_segment(
            [
                egui::pos2(x, center - amplitude),
                egui::pos2(x, center + amplitude),
            ],
            egui::Stroke::new(1.0_f32, egui::Color32::from_rgb(111, 193, 255)),
        );
    }
    for x in [start_x, end_x] {
        painter.line_segment(
            [egui::pos2(x, rect.top()), egui::pos2(x, rect.bottom())],
            egui::Stroke::new(2.0_f32, egui::Color32::from_rgb(255, 190, 65)),
        );
    }

    if response.drag_started() {
        if let Some(pointer) = response.interact_pointer_pos() {
            *drag_handle = Some(usize::from(
                (pointer.x - end_x).abs() < (pointer.x - start_x).abs(),
            ));
        }
    }
    if response.dragged() {
        if let (Some(handle), Some(pointer)) = (*drag_handle, response.interact_pointer_pos()) {
            let fraction = ((pointer.x - rect.left()) / rect.width()).clamp(0.0, 1.0);
            let milliseconds = (fraction * duration as f32) as u64;
            let current_end = if clip.end_ms == 0 {
                duration
            } else {
                clip.end_ms
            };
            if handle == 0 {
                clip.start_ms = milliseconds.min(current_end.saturating_sub(1));
            } else {
                clip.end_ms = if milliseconds >= duration {
                    0
                } else {
                    milliseconds.max(clip.start_ms.saturating_add(1))
                };
            }
        }
    }
    if response.drag_stopped() {
        *drag_handle = None;
    }
    ui.weak("Arraste as bordas para definir o trecho. O arquivo original não é alterado.");
}

#[cfg(windows)]
fn create_tray() -> Result<TrayIcon> {
    let icon = app_icon();
    let icon = Icon::from_rgba(icon.rgba, icon.width, icon.height)?;
    let menu = Menu::new();
    menu.append(&MenuItem::with_id(
        "open",
        "Abrir getSoundBoard",
        true,
        None,
    ))?;
    menu.append(&MenuItem::with_id("stop", "Parar tudo", true, None))?;
    menu.append(&MenuItem::with_id("exit", "Sair", true, None))?;
    Ok(TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_menu_on_left_click(false)
        .with_tooltip("getSoundBoard")
        .with_icon(icon)
        .build()?)
}

#[cfg(windows)]
fn sync_autostart(enabled: bool) -> Result<bool> {
    use winreg::{enums::HKEY_CURRENT_USER, RegKey};

    let root = RegKey::predef(HKEY_CURRENT_USER);
    let (run_key, _) = root.create_subkey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")?;
    let executable = std::env::current_exe()?;
    let legacy_command = format!("\"{}\" --startup", executable.display());
    let legacy_enabled = run_key
        .get_value::<String, _>("getSoundBoard")
        .is_ok_and(|command| command.eq_ignore_ascii_case(&legacy_command));
    let enabled = enabled || legacy_enabled;
    if enabled {
        run_key.set_value(
            "getSoundBoard-Rust",
            &format!("\"{}\" gui --tray", executable.display()),
        )?;
    } else if run_key.get_value::<String, _>("getSoundBoard-Rust").is_ok() {
        run_key.delete_value("getSoundBoard-Rust")?;
    }
    if legacy_enabled {
        run_key.delete_value("getSoundBoard")?;
    }
    Ok(legacy_enabled)
}

pub(super) fn run(start_hidden: bool) -> Result<()> {
    let host = cpal::default_host();
    let inputs: Vec<_> = host
        .input_devices()?
        .filter_map(|device| device.name().ok().map(|name| (name, device)))
        .collect();
    let outputs: Vec<_> = host
        .output_devices()?
        .filter_map(|device| device.name().ok().map(|name| (name, device)))
        .collect();
    let root = directories::BaseDirs::new()
        .ok_or("Pasta de dados indisponível")?
        .data_local_dir()
        .join("getSoundBoard");
    fs::create_dir_all(&root)?;
    let lock = File::options()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(root.join("rust-library.lock"))?;
    lock.try_lock()
        .map_err(|_| "Biblioteca em uso por outra janela")?;
    let clips = load(&root)?;
    let settings_path = root.join("rust-settings.json");
    let mut settings = if settings_path.try_exists()? {
        serde_json::from_reader(File::open(&settings_path)?)?
    } else {
        Settings::default()
    };
    if settings.speakers.is_empty() {
        settings.speakers = host
            .default_output_device()
            .and_then(|device| device.name().ok())
            .or_else(|| outputs.first().map(|(name, _)| name.clone()))
            .unwrap_or_default();
    }
    if settings.microphone.is_empty() {
        settings.microphone = host
            .default_input_device()
            .and_then(|device| device.name().ok())
            .or_else(|| inputs.first().map(|(name, _)| name.clone()))
            .unwrap_or_default();
    }
    if settings.chat_output.is_empty() {
        settings.chat_output = outputs
            .iter()
            .find(|(name, _)| name.to_lowercase().contains("cable input"))
            .map(|(name, _)| name.clone())
            .unwrap_or_default();
    }
    #[cfg(windows)]
    let startup_status = match sync_autostart(settings.start_with_windows) {
        Ok(migrated) => {
            if migrated {
                settings.start_with_windows = true;
                let mut tmp = tempfile::NamedTempFile::new_in(&root)?;
                serde_json::to_writer_pretty(&mut tmp, &settings)?;
                tmp.flush()?;
                tmp.as_file().sync_all()?;
                tmp.persist(&settings_path)?;
            }
            String::new()
        }
        Err(error) => format!("Inicialização automática: {error}"),
    };
    #[cfg(not(windows))]
    let startup_status = String::new();
    #[cfg(windows)]
    let tray = create_tray()?;
    let (waveform_tx, waveform_rx) = mpsc::channel();
    eframe::run_native(
        "getSoundBoard",
        eframe::NativeOptions {
            viewport: egui::ViewportBuilder::default()
                .with_visible(!start_hidden)
                .with_icon(app_icon()),
            ..Default::default()
        },
        Box::new(move |_| {
            let keys = GlobalHotKeyManager::new();
            let status = keys
                .as_ref()
                .err()
                .map(|e| format!("Atalhos globais indisponíveis: {e}"))
                .unwrap_or(startup_status);
            let mut board = Board {
                root,
                _lock: lock,
                clips,
                playing: vec![],
                inputs,
                outputs,
                route: None,
                settings,
                keys: keys.ok(),
                active: HashMap::new(),
                status,
                edit: None,
                recording: None,
                search: String::new(),
                category: String::new(),
                paused: false,
                selected: None,
                waveform: None,
                waveform_loading: None,
                waveform_error: None,
                waveform_tx,
                waveform_rx,
                waveform_drag_handle: None,
                #[cfg(windows)]
                _tray: tray,
                #[cfg(windows)]
                exit_requested: false,
            };
            board.bind();
            if !board.settings.speakers.is_empty() {
                if let Err(e) = board.connect_audio() {
                    board.status = format!("Áudio desconectado: {e}");
                }
            }
            Ok(Box::new(board))
        }),
    )?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_legacy_catalog_and_prefers_rust_edits() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        fs::write(
            root.join("library.json"),
            r#"{"Clips":[{"Title":"Alarme","FileName":"a.wav","Hotkey":"Ctrl+F8"}]}"#,
        )
        .unwrap();
        let items = load(root).unwrap();
        assert_eq!(items[0].path, root.join("Audio/a.wav"));
        assert_eq!(items[0].key, "Ctrl+F8");
        fs::write(root.join("rust-library.json"), "[]").unwrap();
        assert!(load(root).unwrap().is_empty());
    }
}
