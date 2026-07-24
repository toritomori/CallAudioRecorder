using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CallAudioRecorder.Models;
using NAudio.Wave;
using SherpaOnnx;

namespace CallAudioRecorder.Services;

/// <summary>
/// Живая транскрипция двух каналов (микрофон = «Я», система = «Собеседник»).
///
/// Схема: два «насоса» читают tap-потоки 16 kHz mono со скоростью реального времени
/// (ReadFully=true ⇒ тишина идёт нулями, поэтому индекс сэмпла = время записи),
/// каждый кормит свой Silero VAD; готовые речевые сегменты складываются в общую
/// очередь и последовательно распознаются ОДНИМ OfflineRecognizer (GigaAM v3),
/// чтобы не выедать CPU у MP3-пути.
/// </summary>
public sealed class TranscriptionService : IDisposable
{
    private const int Rate = 16000;
    private const int ChunkFrames = 1600; // 100 мс

    /// <summary>Реплики одного спикера с паузой меньше этой склеиваются в один блок (настраивается в UI).</summary>
    public static TimeSpan BubbleMergeGap { get; set; } = TimeSpan.FromSeconds(3.5);

    private sealed record SegmentJob(string Speaker, TimeSpan Start, float[] Samples);

    /// <summary>
    /// Кольцевой буфер последних N секунд канала. Нужен, чтобы вырезать сегмент
    /// с запасом до и после речи: Silero режет «впритык» и съедает первый слог.
    /// </summary>
    private sealed class AudioRing(int capacity)
    {
        private readonly float[] _buf = new float[capacity];
        private long _written;

        public void Write(float[] src, int count)
        {
            for (int i = 0; i < count; i++)
                _buf[(int)((_written + i) % capacity)] = src[i];
            _written += count;
        }

        /// <summary>Вырезает [from, to) в абсолютных индексах сэмплов. null — если данные уже вытеснены.</summary>
        public float[]? Slice(long from, long to)
        {
            from = Math.Max(0, from);
            to = Math.Min(to, _written);
            if (to <= from || _written - from > capacity) return null;

            var result = new float[to - from];
            for (long i = from; i < to; i++)
                result[i - from] = _buf[(int)(i % capacity)];
            return result;
        }
    }

    /// <summary>Сколько аудио добавлять до и после речевого сегмента, чтобы VAD не срезал первый/последний слог.</summary>
    private static readonly TimeSpan SegmentPadding = TimeSpan.FromSeconds(0.3);

    private readonly OfflineRecognizer _recognizer;
    private SpeakerDiarizer? _diarizer;
    private readonly string _vadModelPath;
    private readonly string? _hotwordsFile;
    private readonly List<TranscriptEntry> _entries = new();
    private readonly BlockingCollection<SegmentJob> _queue = new();
    private readonly List<Thread> _threads = new();
    private readonly object _entriesLock = new();

    private volatile bool _running;
    private Exception? _error;

    /// <summary>Новая распознанная реплика (вызывается на фоновом потоке!).</summary>
    public event Action<TranscriptEntry>? EntryRecognized;

    /// <summary>Сколько сегментов ждёт распознавания.</summary>
    public int QueueDepth => _queue.Count;

    public Exception? Error => _error;

    public static string ModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CallAudioRecorder", "models");

    public static string SpeakerModelPath => Path.Combine(ModelsRoot, "speaker_campplus_zh_en.onnx");

    /// <param name="hotwords">
    /// Термины/имена для контекстного смещения — по одному на строку, КИРИЛЛИЦЕЙ
    /// (словарь GigaAM почти не содержит латиницы). Латинское написание восстанавливает
    /// LLM-коррекция, см. <see cref="TranscriptCorrector"/>.
    /// </param>
    /// <param name="diarizeSpeakers">
    /// Различать собеседников по голосу: реплики системного канала помечаются
    /// «Собеседник 1», «Собеседник 2», … вместо общего «Собеседник».
    /// </param>
    public TranscriptionService(IEnumerable<string>? hotwords = null, bool diarizeSpeakers = false)
    {
        var modelDir = Path.Combine(ModelsRoot, "giga-am-v3-punct");
        _vadModelPath = Path.Combine(ModelsRoot, "silero_vad.onnx");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = Rate;
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = 2;

        // Beam search точнее greedy, а запас по CPU огромен (RTF ≈ 0.09).
        // Hotwords работают ТОЛЬКО с transducer + modified_beam_search.
        config.DecodingMethod = "modified_beam_search";
        config.MaxActivePaths = 4;

        _hotwordsFile = WriteHotwordsFile(hotwords);
        if (_hotwordsFile is not null)
        {
            config.HotwordsFile = _hotwordsFile;
            config.HotwordsScore = 2.0f;
        }

        _recognizer = new OfflineRecognizer(config);

        // Диаризация опциональна: нет модели или не создалась — работаем как раньше («Собеседник»).
        if (diarizeSpeakers && File.Exists(SpeakerModelPath))
        {
            try { _diarizer = new SpeakerDiarizer(SpeakerModelPath); }
            catch { _diarizer = null; }
        }
    }

    /// <summary>Пишет hotwords во временный файл (sherpa-onnx принимает только путь). null — если пусто.</summary>
    private static string? WriteHotwordsFile(IEnumerable<string>? hotwords)
    {
        var words = (hotwords ?? Enumerable.Empty<string>())
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .Distinct()
            .ToList();
        if (words.Count == 0) return null;

        var path = Path.Combine(Path.GetTempPath(), $"car_hotwords_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, words, System.Text.Encoding.UTF8);
        return path;
    }

    /// <summary>Запускает насосы обоих каналов и поток распознавания.</summary>
    public void Start(ISampleProvider micTap16k, ISampleProvider systemTap16k, Func<TimeSpan> clock)
    {
        _running = true;

        var micThread = new Thread(() => PumpLoop("Я", micTap16k, clock))
            { IsBackground = true, Name = "MicVadPump" };
        var sysThread = new Thread(() => PumpLoop("Собеседник", systemTap16k, clock))
            { IsBackground = true, Name = "SysVadPump" };
        var recThread = new Thread(RecognizeLoop) { IsBackground = true, Name = "SttWorker" };

        _threads.AddRange(new[] { micThread, sysThread, recThread });
        micThread.Start();
        sysThread.Start();
        recThread.Start();
    }

    /// <summary>
    /// Останавливает насосы, дораспознаёт остаток и возвращает полный транскрипт,
    /// отсортированный по времени.
    /// </summary>
    public IReadOnlyList<TranscriptEntry> StopAndDrain()
    {
        _running = false;
        foreach (var t in _threads.Where(t => t.Name!.EndsWith("Pump") && t.IsAlive))
            t.Join(TimeSpan.FromSeconds(5));

        _queue.CompleteAdding();
        _threads.FirstOrDefault(t => t.Name == "SttWorker")?.Join(TimeSpan.FromMinutes(2));

        lock (_entriesLock)
            return MergeAdjacent(_entries.OrderBy(e => e.StartTime));
    }

    /// <summary>Склеивает подряд идущие реплики одного спикера с паузой &lt; BubbleMergeGap.</summary>
    public static List<TranscriptEntry> MergeAdjacent(IEnumerable<TranscriptEntry> sorted)
    {
        var result = new List<TranscriptEntry>();
        foreach (var entry in sorted)
        {
            if (result.Count > 0 &&
                result[^1].Speaker == entry.Speaker &&
                entry.StartTime - result[^1].End <= BubbleMergeGap)
            {
                result[^1] = result[^1].MergeWith(entry);
            }
            else
            {
                result.Add(entry);
            }
        }
        return result;
    }

    /// <summary>
    /// Насос канала: читает tap со скоростью реального времени, кормит VAD,
    /// готовые сегменты ставит в очередь распознавания.
    /// </summary>
    private void PumpLoop(string speaker, ISampleProvider tap, Func<TimeSpan> clock)
    {
        try
        {
            var vadConfig = new VadModelConfig();
            vadConfig.SileroVad.Model = _vadModelPath;
            vadConfig.SileroVad.Threshold = 0.5f;
            vadConfig.SileroVad.MinSilenceDuration = 0.5f;
            vadConfig.SileroVad.MinSpeechDuration = 0.25f;
            vadConfig.SileroVad.MaxSpeechDuration = 15f;
            vadConfig.SileroVad.WindowSize = 512;
            vadConfig.SampleRate = Rate;
            using var vad = new VoiceActivityDetector(vadConfig, 60);

            var ring = new AudioRing(Rate * 60); // последние 60 с канала
            var buf = new float[ChunkFrames];
            long framesRead = 0;

            while (_running)
            {
                long targetFrames = (long)(clock().TotalSeconds * Rate);
                while (framesRead < targetFrames)
                {
                    int want = (int)Math.Min(ChunkFrames, targetFrames - framesRead);
                    int read = tap.Read(buf, 0, want);
                    if (read <= 0) break;
                    framesRead += read;

                    ring.Write(buf, read);
                    if (read == buf.Length) vad.AcceptWaveform(buf);
                    else vad.AcceptWaveform(buf.AsSpan(0, read).ToArray());

                    DrainVad(vad, speaker, ring);
                }
                Thread.Sleep(50);
            }

            vad.Flush();
            DrainVad(vad, speaker, ring);
        }
        catch (Exception ex)
        {
            _error = ex;
        }
    }

    /// <summary>
    /// Забирает готовые сегменты у VAD и ставит в очередь, расширяя каждый
    /// на SegmentPadding в обе стороны (иначе теряются краевые слоги).
    /// </summary>
    private void DrainVad(VoiceActivityDetector vad, string speaker, AudioRing ring)
    {
        int pad = (int)(SegmentPadding.TotalSeconds * Rate);

        while (!vad.IsEmpty())
        {
            var seg = vad.Front();
            long start = seg.Start;
            long end = start + seg.Samples.Length;
            vad.Pop();

            var padded = ring.Slice(start - pad, end + pad);
            long actualStart = padded is null ? start : Math.Max(0, start - pad);
            var samples = padded ?? (float[])seg.Samples.Clone();

            if (!_queue.IsAddingCompleted)
                _queue.Add(new SegmentJob(speaker,
                    TimeSpan.FromSeconds(actualStart / (double)Rate), samples));
        }
    }

    /// <summary>Единственный поток распознавания: сегменты строго по очереди.</summary>
    private void RecognizeLoop()
    {
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(Rate, job.Samples);
                _recognizer.Decode(stream);
                var text = stream.Result.Text.Trim();
                if (text.Length == 0) continue;

                var speaker = job.Speaker;
                if (_diarizer is not null && speaker == "Собеседник")
                {
                    // Ошибка диаризации не должна ронять транскрипцию — выключаем её и едем дальше.
                    try { speaker = _diarizer.Identify(job.Samples); }
                    catch { _diarizer.Dispose(); _diarizer = null; }
                }

                var entry = new TranscriptEntry(speaker, text, job.Start,
                    TimeSpan.FromSeconds(job.Samples.Length / (double)Rate));
                lock (_entriesLock) _entries.Add(entry);
                EntryRecognized?.Invoke(entry);
            }
        }
        catch (Exception ex)
        {
            _error = ex;
        }
    }

    public void Dispose()
    {
        _running = false;
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        _recognizer.Dispose();
        _diarizer?.Dispose();
        _queue.Dispose();

        if (_hotwordsFile is not null)
        {
            try { File.Delete(_hotwordsFile); } catch { /* временный файл, не критично */ }
        }
    }
}
