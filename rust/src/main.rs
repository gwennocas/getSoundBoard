#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

mod desktop;
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{Device, FromSample, Sample, SampleFormat, SizedSample};
use nnnoiseless::DenoiseState;
use rodio::{
    dynamic_mixer::{mixer, DynamicMixer, DynamicMixerController},
    source::UniformSourceIterator,
    Decoder, OutputStream, Sink, Source,
};
use rtrb::{Consumer, Producer, RingBuffer};
use std::sync::{
    atomic::{AtomicBool, AtomicU32, Ordering},
    Arc,
};
use std::{
    env,
    error::Error,
    fs::File,
    io::{self, BufReader},
    time::Duration,
};

type Result<T> = std::result::Result<T, Box<dyn Error>>;

struct Voice {
    samples: Consumer<f32>,
    rate: u32,
}

impl Iterator for Voice {
    type Item = f32;
    fn next(&mut self) -> Option<f32> {
        Some(self.samples.pop().unwrap_or(0.0))
    }
}

#[derive(Clone, Copy)]
struct VoiceSettings {
    microphone_gain: f32,
    noise_suppression: bool,
    suppression_strength: f32,
    clarity: bool,
}

impl Default for VoiceSettings {
    fn default() -> Self {
        Self {
            microphone_gain: 1.0,
            noise_suppression: false,
            suppression_strength: 0.5,
            clarity: false,
        }
    }
}

struct VoiceControls {
    microphone_gain: AtomicU32,
    noise_suppression: AtomicBool,
    suppression_strength: AtomicU32,
    clarity: AtomicBool,
}

impl VoiceControls {
    fn new(settings: VoiceSettings) -> Arc<Self> {
        Arc::new(Self {
            microphone_gain: AtomicU32::new(settings.microphone_gain.to_bits()),
            noise_suppression: AtomicBool::new(settings.noise_suppression),
            suppression_strength: AtomicU32::new(settings.suppression_strength.to_bits()),
            clarity: AtomicBool::new(settings.clarity),
        })
    }

    fn set(&self, settings: VoiceSettings) {
        self.microphone_gain
            .store(settings.microphone_gain.to_bits(), Ordering::Relaxed);
        self.noise_suppression
            .store(settings.noise_suppression, Ordering::Relaxed);
        self.suppression_strength
            .store(settings.suppression_strength.to_bits(), Ordering::Relaxed);
        self.clarity.store(settings.clarity, Ordering::Relaxed);
    }
}

struct Biquad {
    b0: f32,
    b1: f32,
    b2: f32,
    a1: f32,
    a2: f32,
    x1: f32,
    x2: f32,
    y1: f32,
    y2: f32,
}

impl Biquad {
    fn new(b: [f32; 3], a: [f32; 3]) -> Self {
        Self {
            b0: b[0] / a[0],
            b1: b[1] / a[0],
            b2: b[2] / a[0],
            a1: a[1] / a[0],
            a2: a[2] / a[0],
            x1: 0.0,
            x2: 0.0,
            y1: 0.0,
            y2: 0.0,
        }
    }

    fn high_pass(rate: f32, frequency: f32, q: f32) -> Self {
        let (sin, cos) = (std::f32::consts::TAU * frequency / rate).sin_cos();
        let alpha = sin / (2.0 * q);
        Self::new(
            [(1.0 + cos) / 2.0, -(1.0 + cos), (1.0 + cos) / 2.0],
            [1.0 + alpha, -2.0 * cos, 1.0 - alpha],
        )
    }

    fn peaking(rate: f32, frequency: f32, q: f32, gain_db: f32) -> Self {
        let (sin, cos) = (std::f32::consts::TAU * frequency / rate).sin_cos();
        let alpha = sin / (2.0 * q);
        let a = 10.0_f32.powf(gain_db / 40.0);
        Self::new(
            [1.0 + alpha * a, -2.0 * cos, 1.0 - alpha * a],
            [1.0 + alpha / a, -2.0 * cos, 1.0 - alpha / a],
        )
    }

    fn process(&mut self, x: f32) -> f32 {
        let y = self.b0 * x + self.b1 * self.x1 + self.b2 * self.x2
            - self.a1 * self.y1
            - self.a2 * self.y2;
        self.x2 = self.x1;
        self.x1 = x;
        self.y2 = self.y1;
        self.y1 = y;
        y
    }
}

struct VoicePipeline {
    source: UniformSourceIterator<Voice, f32>,
    controls: Arc<VoiceControls>,
    denoiser: Box<DenoiseState<'static>>,
    input: [f32; DenoiseState::FRAME_SIZE],
    dry: [f32; DenoiseState::FRAME_SIZE],
    previous_dry: [f32; DenoiseState::FRAME_SIZE],
    output: [f32; DenoiseState::FRAME_SIZE],
    frame_pos: usize,
    first_frame: bool,
    last_suppression_enabled: Option<bool>,
    high_pass: Biquad,
    presence: Biquad,
}

impl VoicePipeline {
    fn new(source: Voice, controls: Arc<VoiceControls>) -> Self {
        Self {
            source: UniformSourceIterator::new(source, 1, 48_000),
            controls,
            denoiser: DenoiseState::new(),
            input: [0.0; DenoiseState::FRAME_SIZE],
            dry: [0.0; DenoiseState::FRAME_SIZE],
            previous_dry: [0.0; DenoiseState::FRAME_SIZE],
            output: [0.0; DenoiseState::FRAME_SIZE],
            frame_pos: DenoiseState::FRAME_SIZE,
            first_frame: true,
            last_suppression_enabled: None,
            high_pass: Biquad::high_pass(48_000.0, 85.0, 0.707),
            presence: Biquad::peaking(48_000.0, 3_000.0, 0.9, 2.5),
        }
    }

    fn process_frame(&mut self) {
        let gain =
            f32::from_bits(self.controls.microphone_gain.load(Ordering::Relaxed)).clamp(0.0, 3.0);
        for i in 0..DenoiseState::FRAME_SIZE {
            let sample = (self.source.next().unwrap_or(0.0) * gain).clamp(-1.0, 1.0);
            self.dry[i] = sample;
        }
        let enabled = self.controls.noise_suppression.load(Ordering::Relaxed);
        let strength = if enabled {
            f32::from_bits(self.controls.suppression_strength.load(Ordering::Relaxed))
                .clamp(0.0, 1.0)
        } else {
            0.0
        };
        if strength > 0.0 {
            if self.last_suppression_enabled == Some(false) {
                self.denoiser = DenoiseState::new();
            }
            self.last_suppression_enabled = Some(true);
            for i in 0..DenoiseState::FRAME_SIZE {
                self.input[i] = self.dry[i] * i16::MAX as f32;
            }
            self.denoiser.process_frame(&mut self.output, &self.input);
            if self.first_frame {
                self.output.fill(0.0);
                self.first_frame = false;
            } else {
                for i in 0..DenoiseState::FRAME_SIZE {
                    let denoised = (self.output[i] / i16::MAX as f32).clamp(-1.0, 1.0);
                    self.output[i] =
                        self.previous_dry[i] + (denoised - self.previous_dry[i]) * strength;
                }
            }
        } else {
            self.output.copy_from_slice(&self.dry);
            self.first_frame = true;
            self.last_suppression_enabled = Some(false);
        }
        self.previous_dry.copy_from_slice(&self.dry);
        let clarity = self.controls.clarity.load(Ordering::Relaxed);
        for sample in &mut self.output {
            let filtered = self.presence.process(self.high_pass.process(*sample));
            if clarity {
                *sample = filtered;
            }
        }
        self.frame_pos = 0;
    }
}

impl Iterator for VoicePipeline {
    type Item = f32;

    fn next(&mut self) -> Option<Self::Item> {
        if self.frame_pos == DenoiseState::FRAME_SIZE {
            self.process_frame();
        }
        let sample = self.output[self.frame_pos];
        self.frame_pos += 1;
        Some(sample)
    }
}

impl Source for VoicePipeline {
    fn current_frame_len(&self) -> Option<usize> {
        None
    }
    fn channels(&self) -> u16 {
        1
    }
    fn sample_rate(&self) -> u32 {
        48_000
    }
    fn total_duration(&self) -> Option<Duration> {
        None
    }
}

impl Source for Voice {
    fn current_frame_len(&self) -> Option<usize> {
        None
    }
    fn channels(&self) -> u16 {
        1
    }
    fn sample_rate(&self) -> u32 {
        self.rate
    }
    fn total_duration(&self) -> Option<Duration> {
        None
    }
}

fn capture<T: SizedSample>(
    dev: &Device,
    cfg: &cpal::StreamConfig,
    mut tx: Producer<f32>,
) -> Result<cpal::Stream>
where
    f32: FromSample<T>,
{
    let channels = usize::from(cfg.channels);
    Ok(dev.build_input_stream(
        cfg,
        move |data: &[T], _| {
            for frame in data.chunks_exact(channels) {
                let mono =
                    frame.iter().map(|s| f32::from_sample(*s)).sum::<f32>() / channels as f32;
                let _ = tx.push(mono);
            }
        },
        |e| eprintln!("Captura interrompida: {e}"),
        None,
    )?)
}

fn device(devices: Vec<Device>, arg: &str) -> Result<Device> {
    Ok(devices
        .into_iter()
        .nth(arg.parse::<usize>()?)
        .ok_or("Índice de dispositivo inválido")?)
}

fn levels(args: &str) -> Result<(f32, f32)> {
    let mut values = args.split_whitespace().map(str::parse::<u8>);
    let a = values.next().ok_or("Falta volume do fone")??;
    let b = values.next().ok_or("Falta volume do chat")??;
    if a > 100 || b > 100 || values.next().is_some() {
        return Err("Use dois volumes de 0 a 100".into());
    }
    Ok((a as f32 / 100.0, b as f32 / 100.0))
}

type Bus = Arc<DynamicMixerController<f32>>;

// Mesmo limite por bloco do PeakLimiterSampleProvider do app Windows.
struct Limited {
    mixer: DynamicMixer<f32>,
    block: [f32; 960],
    pos: usize,
}

impl Iterator for Limited {
    type Item = f32;
    fn next(&mut self) -> Option<f32> {
        if self.pos == self.block.len() {
            let mut peak = 0.98_f32;
            for sample in &mut self.block {
                *sample = self.mixer.next().unwrap_or(0.0);
                peak = peak.max(sample.abs());
            }
            for sample in &mut self.block {
                *sample *= 0.98 / peak;
            }
            self.pos = 0;
        }
        let sample = self.block[self.pos];
        self.pos += 1;
        Some(sample)
    }
}

impl Source for Limited {
    fn current_frame_len(&self) -> Option<usize> {
        None
    }
    fn channels(&self) -> u16 {
        2
    }
    fn sample_rate(&self) -> u32 {
        48000
    }
    fn total_duration(&self) -> Option<Duration> {
        None
    }
}

fn bus() -> (Bus, Limited) {
    let (bus, mixer) = mixer(2, 48000);
    (
        bus,
        Limited {
            mixer,
            block: [0.0; 960],
            pos: 960,
        },
    )
}

fn route_clip<S>(
    audio: S,
    monitor: Option<&Bus>,
    chat: Option<&Bus>,
    gains: (f32, f32),
) -> Vec<Sink>
where
    S: Source<Item = f32> + Clone + Send + 'static,
{
    let route = |bus: &Bus, gain, audio| {
        let (sink, queue) = Sink::new_idle();
        sink.set_volume(gain);
        sink.append(audio);
        bus.add(queue);
        sink
    };
    let mut sinks = Vec::new();
    if let Some(monitor) = monitor {
        sinks.push(route(monitor, gains.0, audio.clone()));
    }
    if let Some(chat) = chat {
        sinks.push(route(chat, gains.1, audio));
    }
    sinks
}

fn play(path: &str, monitor: &Bus, chat: Option<&Bus>, gains: (f32, f32)) -> Result<Vec<Sink>> {
    play_routed(path, Some(monitor), chat, gains, false)
}

fn play_routed(
    path: &str,
    monitor: Option<&Bus>,
    chat: Option<&Bus>,
    gains: (f32, f32),
    repeat: bool,
) -> Result<Vec<Sink>> {
    play_routed_trimmed(path, monitor, chat, gains, repeat, 0, 0)
}

fn play_routed_trimmed(
    path: &str,
    monitor: Option<&Bus>,
    chat: Option<&Bus>,
    gains: (f32, f32),
    repeat: bool,
    start_ms: u64,
    end_ms: u64,
) -> Result<Vec<Sink>> {
    if monitor.is_none() && chat.is_none() {
        return Err("Escolha pelo menos um destino de áudio".into());
    }
    let decoder = Decoder::new(BufReader::new(File::open(path)?))?;
    if end_ms != 0 && end_ms <= start_ms {
        return Err("O fim do trecho precisa vir depois do início".into());
    }
    let audio = decoder
        .convert_samples::<f32>()
        .skip_duration(Duration::from_millis(start_ms))
        .buffered();
    if end_ms == 0 {
        if repeat {
            Ok(route_clip(audio.repeat_infinite(), monitor, chat, gains))
        } else {
            Ok(route_clip(audio, monitor, chat, gains))
        }
    } else {
        let audio = audio
            .take_duration(Duration::from_millis(end_ms - start_ms))
            .buffered();
        if repeat {
            Ok(route_clip(audio.repeat_infinite(), monitor, chat, gains))
        } else {
            Ok(route_clip(audio, monitor, chat, gains))
        }
    }
}
fn start_voice(
    mic: &Device,
    chat_bus: &Bus,
    settings: VoiceSettings,
) -> Result<(cpal::Stream, Arc<VoiceControls>)> {
    let format = mic.default_input_config()?;
    let cfg = format.config();
    let (tx, samples) = RingBuffer::new((cfg.sample_rate.0 / 5) as usize);
    let stream = match format.sample_format() {
        SampleFormat::F32 => capture::<f32>(mic, &cfg, tx)?,
        SampleFormat::I16 => capture::<i16>(mic, &cfg, tx)?,
        SampleFormat::U16 => capture::<u16>(mic, &cfg, tx)?,
        SampleFormat::I32 => capture::<i32>(mic, &cfg, tx)?,
        other => return Err(format!("Formato de microfone incompatível: {other}").into()),
    };
    let controls = VoiceControls::new(settings);
    chat_bus.add(VoicePipeline::new(
        Voice {
            samples,
            rate: cfg.sample_rate.0,
        },
        controls.clone(),
    ));
    stream.play()?;
    Ok((stream, controls))
}

fn main() {
    if let Err(error) = run() {
        #[cfg(windows)]
        if !cfg!(debug_assertions) {
            rfd::MessageDialog::new()
                .set_title("getSoundBoard")
                .set_description(error.to_string())
                .set_level(rfd::MessageLevel::Error)
                .show();
        } else {
            eprintln!("{error}");
        }
        #[cfg(not(windows))]
        eprintln!("{error}");
    }
}

fn run() -> Result<()> {
    let mut args: Vec<_> = env::args().skip(1).collect();
    let gui_argument = args.first().is_some_and(|a| a == "gui");
    let legacy_startup = args.first().is_some_and(|a| a == "--startup");
    let gui = gui_argument
        || legacy_startup
        || (cfg!(all(windows, not(debug_assertions))) && args.is_empty());
    if gui {
        if gui_argument || legacy_startup {
            args.remove(0);
        }
        let start_hidden = legacy_startup || args.first().is_some_and(|arg| arg == "--tray");
        #[cfg(not(windows))]
        if start_hidden {
            return Err("A opção '--tray' está disponível apenas no Windows.".into());
        }
        if args.first().is_some_and(|arg| arg == "--tray") {
            args.remove(0);
        }
        if !args.is_empty() {
            return Err("Use 'gui' ou 'gui --tray'.".into());
        }
        return desktop::run(start_hidden);
    }
    let host = cpal::default_host();
    let inputs: Vec<_> = host.input_devices()?.collect();
    let outputs: Vec<_> = host.output_devices()?.collect();
    if args.is_empty() {
        for (kind, devices) in [("Entrada", inputs), ("Saída", outputs)] {
            for (id, dev) in devices.iter().enumerate() {
                println!("{kind} {id}: {}", dev.name()?);
            }
        }
        println!("Uso: getsoundboard local <saída> ou getsoundboard <entrada-microfone> <saída-fone> <saída-virtual>");
        return Ok(());
    }
    let (monitor_id, route) = match args.as_slice() {
        [mode, output] if mode == "local" => (output, None),
        [mic, output, chat] => (output, Some((mic, chat))),
        _ => return Err("Use local <saída> ou três índices".into()),
    };
    let monitor_id: usize = monitor_id.parse()?;
    let (_monitor, monitor) =
        OutputStream::try_from_device(outputs.get(monitor_id).ok_or("Fone inválido")?)?;
    let (monitor_bus, monitor_mix) = bus();
    monitor.play_raw(monitor_mix)?;
    let (chat_bus, chat_mix) = bus();
    let _route = if let Some((mic, chat)) = route {
        let chat_id: usize = chat.parse()?;
        if monitor_id == chat_id {
            return Err("Selecione saídas distintas".into());
        }
        let (stream, output) =
            OutputStream::try_from_device(outputs.get(chat_id).ok_or("Saída virtual inválida")?)?;
        output.play_raw(chat_mix)?;
        let mic = device(inputs, mic)?;
        let (capture, _controls) = start_voice(&mic, &chat_bus, VoiceSettings::default())?;
        println!("Microfone ligado.");
        Some((stream, capture))
    } else {
        None
    };
    println!("Comandos: tocar caminho, volume fone chat (0–100), parar, sair.");
    let mut clips: Vec<Vec<Sink>> = Vec::new();
    let mut gains = (0.35, 0.70);
    for line in io::stdin().lines() {
        let line = line?;
        clips.retain(|pair| pair.iter().any(|s| !s.empty()));
        match line.trim() {
            "sair" => break,
            "parar" => clips.clear(),
            cmd if cmd.starts_with("tocar ") => {
                match play(
                    cmd[6..].trim(),
                    &monitor_bus,
                    _route.as_ref().map(|_| &chat_bus),
                    gains,
                ) {
                    Ok(pair) => clips.push(pair),
                    Err(e) => eprintln!("{e}"),
                }
            }
            cmd if cmd.starts_with("volume ") => match levels(&cmd[7..]) {
                Ok(v) => {
                    gains = v;
                    for pair in &clips {
                        pair[0].set_volume(v.0);
                        if let Some(chat) = pair.get(1) {
                            chat.set_volume(v.1);
                        }
                    }
                }
                Err(e) => eprintln!("{e}"),
            },
            _ => eprintln!("Comando desconhecido"),
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn overlap_routes_voice_only_to_chat_and_limits_peaks() {
        let (monitor, mut speakers) = bus();
        let (chat, mut virtual_mic) = bus();
        let signal = |v| rodio::buffer::SamplesBuffer::new(2, 48000, vec![v; 9600]);
        chat.add(signal(0.5));
        let clips: Vec<_> = (0..2)
            .map(|_| route_clip(signal(0.5), Some(&monitor), Some(&chat), (0.25, 0.75)))
            .collect();
        assert!((speakers.next().unwrap() - 0.25).abs() < 0.0001);
        assert!((virtual_mic.next().unwrap() - 0.98).abs() < 0.0001);
        drop(clips);
        assert_eq!(speakers.nth(4800), Some(0.0));
        assert_eq!(virtual_mic.nth(4800), Some(0.5));
    }

    #[test]
    fn independent_levels() {
        assert_eq!(levels("35 70").unwrap(), (0.35, 0.70));
        assert!(levels("101 30").is_err());
    }
}
