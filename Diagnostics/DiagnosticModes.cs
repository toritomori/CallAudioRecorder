using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CallAudioRecorder.Services;

namespace CallAudioRecorder.Diagnostics;

/// <summary>
/// Скрытые режимы самопроверки: <c>CallAudioRecorder.exe --флаг &lt;лог-файл&gt; …</c>.
/// Приложение — WinExe без консоли, поэтому каждый режим пишет отчёт в лог-файл,
/// первая строка которого — OK или FAIL.
///
/// Здесь только таблица «флаг → обработчик»; сами режимы разложены по файлам на тему:
/// аудио, распознавание, диаризация, LLM, текст, вёрстка. Пробники (<c>--stt-test</c>,
/// <c>--diar-test</c>, <c>--diar-file</c>) есть только в Debug-сборке.
/// </summary>
internal static class DiagnosticModes
{
    /// <param name="MinArgs">Сколько аргументов нужно вместе с самим флагом; меньше — режим не запускается.</param>
    private sealed record Mode(int MinArgs, Action<string[]> Run);

    private static readonly Dictionary<string, Mode> Modes = new(StringComparer.Ordinal)
    {
        // --selftest <лог> [секунды] — запись без окна, проверка аудиотракта
        ["--selftest"] = new(2, a => AudioChecks.SelfTest(a[1], IntArg(a, 2, 3))),

        // --proc-loopback-test <лог> [PID] [секунды] [all|direct|engine] — звук одного приложения; PID 0 — список окон
        ["--proc-loopback-test"] = new(2, a => AudioChecks.ProcessLoopback(
            a[1], IntArg(a, 2, 0), IntArg(a, 3, 5), a.Length >= 5 ? a[4] : "all")),

        // --stt-selftest <лог> [секунды] [ru|en] — запись + живая транскрипция
        ["--stt-selftest"] = new(2, a => SttChecks.LiveTranscription(a[1], IntArg(a, 2, 15), LanguageArg(a, 3))),

        // --summary-test <лог> <модель> — итоги по тестовому транскрипту через Ollama
        ["--summary-test"] = new(3, a => RunAsync(() => LlmChecks.Summary(a[1], a[2]))),

        // --fix-test <лог> <модель> [ru|en] — LLM-коррекция искажённых терминов
        ["--fix-test"] = new(3, a => RunAsync(() => LlmChecks.Fix(a[1], a[2], LanguageArg(a, 3)))),

        // --summary-long-test <лог> <модель> [минуты] [ru|en] — окно контекста на длинной встрече
        ["--summary-long-test"] = new(3, a => RunAsync(() =>
            LlmChecks.SummaryLong(a[1], a[2], IntArg(a, 3, 60), LanguageArg(a, 4)))),

        // --names-test <лог> <файл .транскрипт.md> <модель> [ru|en] — какая метка чьим именем становится
        ["--names-test"] = new(4, a => RunAsync(() => LlmChecks.Names(a[1], a[2], a[3], LanguageArg(a, 4)))),

        // --canon-test <лог> <файл .транскрипт.md или папка> — канонизация терминов и версий на корпусе
        ["--canon-test"] = new(3, a => TextChecks.Canon(a[1], a[2])),

        // --ui-shot <png> <лог> — снимок окна и проверка вёрстки на наложения
        ["--ui-shot"] = new(3, a => UiShot.Run(a[1], a[2])),

#if DEBUG
        // --stt-test <лог> <wav> [ru|en] — RTF распознавания
        ["--stt-test"] = new(3, a => SttSpike.Run(a[1], a[2], LanguageArg(a, 3))),

        // --diar-test <лог> <wav1> <wav2> — два голоса → «Собеседник 1»/«Собеседник 2»
        ["--diar-test"] = new(4, a => DiarSpike.Run(a[1], a[2], a[3])),

        // --diar-file <лог> <mp3 или wav> [порог] [порог-для-осколков] — консолидация профилей на записи
        ["--diar-file"] = new(3, a => DiarSpike.RunFile(a[1], a[2], FloatArg(a, 3), FloatArg(a, 4))),
#endif
    };

    /// <summary>Запускает режим, если первый аргумент — его флаг. false — обычный запуск с окном.</summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || !Modes.TryGetValue(args[0], out var mode) || args.Length < mode.MinArgs)
            return false;

        mode.Run(args);
        return true;
    }

    /// <summary>Task.Run — чтобы await внутри режима не пытался вернуться в заблокированный Dispatcher.</summary>
    private static void RunAsync(Func<Task> run) => Task.Run(run).GetAwaiter().GetResult();

    private static int IntArg(string[] args, int index, int fallback) =>
        args.Length > index && int.TryParse(args[index], out var value) ? value : fallback;

    private static float? FloatArg(string[] args, int index) =>
        args.Length > index && float.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Язык из аргументов командной строки: «en»/«english» → английский, иначе русский.</summary>
    private static LanguageProfile LanguageArg(string[] args, int index) =>
        args.Length > index && args[index].StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? Languages.English
            : Languages.Russian;
}
