using System.Buffers.Binary;
using Concentus;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace GetSoundBoard.Services;

/// <summary>Adapts a single-stream Ogg/Opus file to NAudio's PCM wave-stream interface.</summary>
public sealed class OpusOggWaveReader : WaveStream
{
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private readonly FileStream _file;
    private readonly OpusOggReadStream _reader;
    private readonly WaveFormat _waveFormat = new(OutputSampleRate, 16, OutputChannels);
    private readonly long _length;
    private readonly long _sampleFramesToRead;
    private long _sampleFramesRemaining;
    private int _samplesToSkip;
    private short[]? _packet;
    private int _packetPosition;
    private long _position;

    public OpusOggWaveReader(string path)
    {
        _file = File.OpenRead(path);
        try
        {
            var (isOpus, preSkip) = ReadOpusHeader(path);
            if (!isOpus)
                throw new InvalidDataException("O arquivo não contém um cabeçalho Ogg/Opus válido.");

            _reader = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(OutputSampleRate, OutputChannels), _file);
            if (!string.IsNullOrEmpty(_reader.LastError) || !_reader.HasNextPacket)
                throw new InvalidDataException(_reader.LastError ?? "O arquivo Ogg não contém áudio Opus válido.");

            _sampleFramesToRead = Math.Max(0, _reader.GranuleCount - preSkip);
            if (_sampleFramesToRead == 0)
                throw new InvalidDataException("O arquivo Ogg/Opus não contém amostras de áudio.");

            _sampleFramesRemaining = _sampleFramesToRead;
            _samplesToSkip = preSkip * OutputChannels;
            _length = _sampleFramesToRead * _waveFormat.BlockAlign;
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public override WaveFormat WaveFormat => _waveFormat;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value != _position)
                throw new NotSupportedException("Não é possível buscar dentro de um arquivo Ogg/Opus durante a reprodução.");
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var usableCount = count - count % _waveFormat.BlockAlign;
        var written = 0;
        while (written < usableCount && _sampleFramesRemaining > 0)
        {
            if (_packet is null || _packetPosition >= _packet.Length)
            {
                if (!_reader.HasNextPacket)
                    break;

                _packet = _reader.DecodeNextPacket();
                _packetPosition = 0;
                if (_packet is null || _packet.Length == 0)
                {
                    if (!string.IsNullOrEmpty(_reader.LastError))
                        throw new InvalidDataException($"Não foi possível decodificar o áudio Opus: {_reader.LastError}");
                    continue;
                }
            }

            if (_samplesToSkip > 0)
            {
                var skipped = Math.Min(_packet.Length - _packetPosition, _samplesToSkip);
                _packetPosition += skipped;
                _samplesToSkip -= skipped;
                continue;
            }

            var remainingSamples = _sampleFramesRemaining * OutputChannels;
            var samplesToCopy = (int)Math.Min(
                Math.Min(_packet.Length - _packetPosition, (usableCount - written) / sizeof(short)),
                remainingSamples);
            for (var i = 0; i < samplesToCopy; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    buffer.AsSpan(offset + written, sizeof(short)),
                    _packet[_packetPosition++]);
                written += sizeof(short);
                _position += sizeof(short);
            }
            _sampleFramesRemaining -= samplesToCopy / OutputChannels;
        }

        return written;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Close();
            _file.Dispose();
        }
        base.Dispose(disposing);
    }

    private static (bool IsOpus, int PreSkip) ReadOpusHeader(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = new byte[(int)Math.Min(stream.Length, 65536)];
        var read = stream.Read(bytes, 0, bytes.Length);
        var headerIndex = bytes.AsSpan(0, read).IndexOf("OpusHead"u8);
        if (headerIndex < 0 || headerIndex + 12 > read)
            return (false, 0);

        var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(headerIndex + 10, sizeof(ushort)));
        return (true, preSkip);
    }
}
