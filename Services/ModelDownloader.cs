using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CallAudioRecorder.Services;

/// <summary>
/// Загрузка моделей распознавания при первом запуске выбранного языка.
///
/// Общие для всех языков — Silero VAD и CAM++ (диаризация), ~30 МБ. Модель распознавания
/// докачивается под язык: GigaAM v3 (~235 МБ, русский) или Parakeet TDT 0.6B v2 (~660 МБ,
/// английский). Модель другого языка не качается, пока его не выберут.
/// </summary>
public static class ModelDownloader
{
    private const string VadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";
    // Внимание: опечатка «recongition» — так реально называется тег релиза, не «исправлять».
    private const string SpeakerModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/" +
        "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";

    private const string GigaAmRepo = "csukuangfj/sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16";
    private const string ParakeetRepo = "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8";

    // файл → (имя в репозитории, относительный путь у нас, минимальный ожидаемый размер,
    //          прямой URL или null = HuggingFace + зеркало)
    private sealed record ModelFile(string Name, string LocalRel, long MinBytes, string? DirectUrl = null);

    /// <summary>Нужны при любом языке: детектор речи и модель голосовых embedding'ов.</summary>
    private static readonly ModelFile[] Common =
    [
        new("silero_vad.onnx", @"silero_vad.onnx", 300_000, VadUrl),
        new("speaker_campplus_zh_en.onnx", @"speaker_campplus_zh_en.onnx", 25_000_000, SpeakerModelUrl),
    ];

    private static (string Repo, ModelFile[] Files) ModelsFor(LanguageProfile language) =>
        language.Language == TranscriptionLanguage.English
            ? (ParakeetRepo,
              [
                  new("encoder.int8.onnx", $@"{language.ModelDir}\encoder.int8.onnx", 600_000_000),
                  new("decoder.int8.onnx", $@"{language.ModelDir}\decoder.int8.onnx",   5_000_000),
                  new("joiner.int8.onnx",  $@"{language.ModelDir}\joiner.int8.onnx",    1_000_000),
                  new("tokens.txt",        $@"{language.ModelDir}\tokens.txt",              5_000),
              ])
            : (GigaAmRepo,
              [
                  new("encoder.int8.onnx", $@"{language.ModelDir}\encoder.int8.onnx", 200_000_000),
                  new("decoder.onnx",      $@"{language.ModelDir}\decoder.onnx",        3_000_000),
                  new("joiner.onnx",       $@"{language.ModelDir}\joiner.onnx",         1_000_000),
                  new("tokens.txt",        $@"{language.ModelDir}\tokens.txt",              5_000),
              ]);

    private static IEnumerable<(ModelFile File, string Repo)> AllFiles(LanguageProfile language)
    {
        var (repo, files) = ModelsFor(language);
        foreach (var f in Common) yield return (f, repo);
        foreach (var f in files) yield return (f, repo);
    }

    /// <summary>Все ли файлы моделей для этого языка на месте и правдоподобного размера.</summary>
    public static bool ModelsPresent(LanguageProfile? language = null)
    {
        language ??= Languages.Russian;
        foreach (var (f, _) in AllFiles(language))
        {
            var path = Path.Combine(TranscriptionService.ModelsRoot, f.LocalRel);
            if (!File.Exists(path) || new FileInfo(path).Length < f.MinBytes) return false;
        }
        return true;
    }

    /// <summary>Сколько мегабайт осталось докачать для этого языка (0 — всё на месте).</summary>
    public static long MissingMegabytes(LanguageProfile language) =>
        AllFiles(language)
            .Where(x =>
            {
                var path = Path.Combine(TranscriptionService.ModelsRoot, x.File.LocalRel);
                return !File.Exists(path) || new FileInfo(path).Length < x.File.MinBytes;
            })
            .Sum(x => x.File.MinBytes) / 1_000_000;

    /// <summary>Докачивает недостающие файлы выбранного языка с прогрессом («имя файла, процент»).</summary>
    public static async Task EnsureAsync(IProgress<string> progress, LanguageProfile? language = null,
        CancellationToken ct = default)
    {
        language ??= Languages.Russian;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        foreach (var (f, repo) in AllFiles(language))
        {
            var path = Path.Combine(TranscriptionService.ModelsRoot, f.LocalRel);
            if (File.Exists(path) && new FileInfo(path).Length >= f.MinBytes) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var urls = f.DirectUrl is not null
                ? new[] { f.DirectUrl }
                : new[]
                {
                    $"https://huggingface.co/{repo}/resolve/main/{f.Name}",
                    $"https://hf-mirror.com/{repo}/resolve/main/{f.Name}",
                };

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
