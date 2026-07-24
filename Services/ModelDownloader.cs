using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CallAudioRecorder.Services;

/// <summary>
/// Загрузка моделей распознавания при первом запуске:
/// GigaAM v3 (transducer + пунктуация) + Silero VAD, всего ~235 МБ.
/// </summary>
public static class ModelDownloader
{
    private const string HfBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16/resolve/main";
    private const string HfMirrorBase =
        "https://hf-mirror.com/csukuangfj/sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16/resolve/main";
    private const string VadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";
    // Внимание: опечатка «recongition» — так реально называется тег релиза, не «исправлять».
    private const string SpeakerModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/" +
        "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";

    // файл → (относительный путь у нас, минимальный ожидаемый размер для валидации,
    //          прямой URL или null = HuggingFace + зеркало)
    private static readonly (string Name, string LocalRel, long MinBytes, string? DirectUrl)[] Files =
    {
        ("encoder.int8.onnx", @"giga-am-v3-punct\encoder.int8.onnx", 200_000_000, null),
        ("decoder.onnx",      @"giga-am-v3-punct\decoder.onnx",        3_000_000, null),
        ("joiner.onnx",       @"giga-am-v3-punct\joiner.onnx",         1_000_000, null),
        ("tokens.txt",        @"giga-am-v3-punct\tokens.txt",              5_000, null),
        ("silero_vad.onnx",   @"silero_vad.onnx",                        300_000, VadUrl),
        ("speaker_campplus_zh_en.onnx", @"speaker_campplus_zh_en.onnx", 25_000_000, SpeakerModelUrl),
    };

    /// <summary>Все ли файлы моделей на месте и правдоподобного размера.</summary>
    public static bool ModelsPresent()
    {
        foreach (var f in Files)
        {
            var path = Path.Combine(TranscriptionService.ModelsRoot, f.LocalRel);
            if (!File.Exists(path) || new FileInfo(path).Length < f.MinBytes) return false;
        }
        return true;
    }

    /// <summary>Докачивает недостающие файлы с прогрессом («имя файла, процент»).</summary>
    public static async Task EnsureAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        foreach (var f in Files)
        {
            var path = Path.Combine(TranscriptionService.ModelsRoot, f.LocalRel);
            if (File.Exists(path) && new FileInfo(path).Length >= f.MinBytes) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var urls = f.DirectUrl is not null
                ? new[] { f.DirectUrl }
                : new[] { $"{HfBase}/{f.Name}", $"{HfMirrorBase}/{f.Name}" };

            Exception? last = null;
            foreach (var url in urls)
            {
                try
                {
                    await DownloadAsync(http, url, path, f.Name, progress, ct);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                }
            }
            if (last is not null)
                throw new IOException($"Не удалось скачать {f.Name}: {last.Message}", last);

            if (new FileInfo(path).Length < f.MinBytes)
                throw new IOException($"Файл {f.Name} скачался неполным — попробуйте ещё раз.");
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string finalPath,
        string name, IProgress<string> progress, CancellationToken ct)
    {
        var tmp = finalPath + ".partial";
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int read;
            var lastPct = -1;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    int pct = (int)(done * 100 / total);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        progress.Report($"Загрузка {name}: {pct}%");
                    }
                }
            }
        }
        File.Move(tmp, finalPath, overwrite: true);
    }
}
