using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CallAudioRecorder;

/// <summary>
/// Ядро записи: системный звук (WASAPI loopback) + микрофон → микс 48 kHz stereo → MP3.
///
/// Схема:
///   WasapiLoopbackCapture ──► BufferedWaveProvider ──► ресемпл/стерео ──► VolumeSampleProvider ─┐
///                                                                                               ├─► MixingSampleProvider ─► 16-bit PCM ─► LameMP3FileWriter
///   WasapiCapture (микрофон) ► BufferedWaveProvider ──► ресемпл/стерео ──► VolumeSampleProvider ─┘
///
/// Поток записи вытягивает из микшера ровно столько кадров, сколько прошло по
/// часам Stopwatch (48000 кадров/с) — поэтому паузы в системном звуке
/// (loopback не отдаёт данных при тишине) заполняются тишиной, а не «съедаются».
/// </summary>
public sealed class RecordingEngine : IDisposable
{
    private const int MixRate = 48000;
    private const int MixChannels = 2;
    private const int TapRate = 16000; // частота для распознавания речи

    private readonly IWaveIn _systemCapture;
    private readonly WasapiCapture _micCapture;
    private readonly BufferedWaveProvider _systemBuffer;
    private readonly BufferedWaveProvider _micBuffer;
    private readonly BufferedWaveProvider? _systemTapBuffer;
    private readonly BufferedWaveProvider? _micTapBuffer;
    private readonly VolumeSampleProvider _systemVolume;
    private readonly VolumeSampleProvider _micVolume;
    private readonly MixingSampleProvider _mixer;
    private readonly IWaveProvider _pcm16;
    private readonly LameMP3FileWriter _writer;
    private readonly Stream _fileStream;
    private readonly Thread _writeThread;
    private readonly Stopwatch _clock = new();

    private volatile bool _running;
    private volatile float _systemPeak;
    private volatile float _micPeak;
    private Exception? _error;

    public string OutputPath { get; }
    public TimeSpan Elapsed => _clock.Elapsed;
    /// <summary>Пиковый уровень системного звука 0..1 (сбрасывается при чтении).</summary>
    public float SystemPeak { get { var p = _systemPeak; _systemPeak = 0; return p; } }
    /// <summary>Пиковый уровень микрофона 0..1 (сбрасывается при чтении).</summary>
    public float MicPeak { get { var p = _micPeak; _micPeak = 0; return p; } }
    /// <summary>Ошибка фонового потока записи, если случилась.</summary>
    public Exception? Error => _error;

    public float SystemGain
    {
        get => _systemVolume.Volume;
        set => _systemVolume.Volume = value;
    }

    public float MicGain
    {
        get => _micVolume.Volume;
        set => _micVolume.Volume = value;
    }

    /// <summary>Поток микрофона 16 kHz mono для распознавания (если tap'ы включены).</summary>
    public ISampleProvider? MicTap16k { get; }
    /// <summary>Поток системного звука 16 kHz mono для распознавания (если tap'ы включены).</summary>
    public ISampleProvider? SystemTap16k { get; }

    /// <param name="renderDevice">
    /// Устройство вывода для записи всего системного звука. Не нужно, если задан systemProcessId.
    /// </param>
    /// <param name="systemProcessId">
    /// PID приложения, звук которого писать вместо всего устройства (см. ProcessLoopbackCapture).
    /// </param>
    public RecordingEngine(MMDevice? renderDevice, MMDevice captureDevice, string outputPath, int bitrateKbps,
        bool enableTranscriptionTaps = false, int? systemProcessId = null)
    {
        OutputPath = outputPath;

        _systemCapture = systemProcessId is int pid
            ? new Services.ProcessLoopbackCapture(pid)
            : new WasapiLoopbackCapture(renderDevice ?? throw new ArgumentNullException(nameof(renderDevice),
                "Нужно устройство вывода или приложение-источник."));
        _micCapture = new WasapiCapture(captureDevice);

        _systemBuffer = CreateBuffer(_systemCapture.WaveFormat);
        _micBuffer = CreateBuffer(_micCapture.WaveFormat);

        if (enableTranscriptionTaps)
        {
            _systemTapBuffer = CreateBuffer(_systemCapture.WaveFormat, seconds: 10);
            _micTapBuffer = CreateBuffer(_micCapture.WaveFormat, seconds: 10);
            SystemTap16k = PrepareTap(_systemTapBuffer);
            MicTap16k = PrepareTap(_micTapBuffer);
        }

        _systemVolume = new VolumeSampleProvider(PrepareInput(_systemBuffer));
        _micVolume = new VolumeSampleProvider(PrepareInput(_micBuffer));

        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(MixRate, MixChannels))
        {
            ReadFully = true // при пустых буферах отдаёт тишину — потоки не «разъезжаются»
        };
        _mixer.AddMixerInput(_systemVolume);
        _mixer.AddMixerInput(_micVolume);

        _pcm16 = new SampleToWaveProvider16(_mixer);

        _systemCapture.DataAvailable += (_, e) =>
        {
            _systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _systemTapBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            float max = ComputePeak(e, _systemCapture.WaveFormat);
            if (max > _systemPeak) _systemPeak = max;
        };
        _micCapture.DataAvailable += (_, e) =>
        {
            _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _micTapBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            float max = ComputePeak(e, _micCapture.WaveFormat);
            if (max > _micPeak) _micPeak = max;
        };

        _fileStream = File.Create(outputPath);
        _writer = new LameMP3FileWriter(_fileStream, _pcm16.WaveFormat, bitrateKbps);

        _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "Mp3WriteLoop" };
    }

    public void Start()
    {
        _running = true;
        _systemCapture.StartRecording();
        _micCapture.StartRecording();
        _clock.Start();
        _writeThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _clock.Stop();
        _systemCapture.StopRecording();
        _micCapture.StopRecording();
        if (_writeThread.IsAlive)
            _writeThread.Join(TimeSpan.FromSeconds(5));
        _writer.Flush();
    }

    private static BufferedWaveProvider CreateBuffer(WaveFormat format, int seconds = 5) => new(format)
    {
        BufferDuration = TimeSpan.FromSeconds(seconds),
        DiscardOnBufferOverflow = true,
        ReadFully = true
    };

    /// <summary>Приводит поток захвата к формату распознавания: 16 kHz, mono, float.</summary>
    private static ISampleProvider PrepareTap(BufferedWaveProvider buffer)
    {
        ISampleProvider sp = buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sp.WaveFormat.SampleRate != TapRate)
            sp = new WdlResamplingSampleProvider(sp, TapRate);
        return sp;
    }

    /// <summary>Приводит поток захвата к формату микшера: 48 kHz, 2 канала, float.</summary>
    private static ISampleProvider PrepareInput(BufferedWaveProvider buffer)
    {
        ISampleProvider sp = buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1)
            sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
            throw new NotSupportedException(
                $"Устройства с {sp.WaveFormat.Channels} каналами не поддерживаются. Выберите стерео- или моно-устройство.");
        if (sp.WaveFormat.SampleRate != MixRate)
            sp = new WdlResamplingSampleProvider(sp, MixRate);
        return sp;
    }

    /// <summary>
    /// Пишет MP3 со скоростью реального времени: на каждом тике вытягивает из микшера
    /// столько кадров, сколько «положено» по прошедшему времени.
    /// </summary>
    private void WriteLoop()
    {
        const int bytesPerFrame = MixChannels * 2; // 16 бит
        var buffer = new byte[MixRate * bytesPerFrame]; // запас на 1 секунду
        long framesWritten = 0;

        try
        {
            while (_running)
            {
                long targetFrames = (long)(_clock.Elapsed.TotalSeconds * MixRate);
                int frames = (int)Math.Min(targetFrames - framesWritten, buffer.Length / bytesPerFrame);
                if (frames > 0)
                {
                    int read = _pcm16.Read(buffer, 0, frames * bytesPerFrame);
                    if (read > 0)
                    {
                        _writer.Write(buffer, 0, read);
                        framesWritten += read / bytesPerFrame;
                    }
                }
                Thread.Sleep(20);
            }
            // дописать хвост, накопившийся к моменту остановки
            long tail = (long)(_clock.Elapsed.TotalSeconds * MixRate) - framesWritten;
            while (tail > 0)
            {
                int frames = (int)Math.Min(tail, buffer.Length / bytesPerFrame);
                int read = _pcm16.Read(buffer, 0, frames * bytesPerFrame);
                if (read <= 0) break;
                _writer.Write(buffer, 0, read);
                tail -= read / bytesPerFrame;
            }
        }
        catch (Exception ex)
        {
            _error = ex;
            _running = false;
        }
    }

    /// <summary>Считает пиковый уровень по буферу захвата (float32 или PCM16).</summary>
    private static float ComputePeak(WaveInEventArgs e, WaveFormat format)
    {
        float max = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
            {
                float v = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                if (v > max) max = v;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                float v = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                if (v > max) max = v;
            }
        }
        return max;
    }

    public void Dispose()
    {
        _running = false;
        _systemCapture.Dispose();
        _micCapture.Dispose();
        _writer.Dispose();
        _fileStream.Dispose();
    }
}
