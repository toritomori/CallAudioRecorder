using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CallAudioRecorder.Models;

namespace CallAudioRecorder.Diagnostics;

/// <summary>LLM-слой на живом Ollama: итоги, коррекция, имена участников.</summary>
internal static class LlmChecks
{
    /// <summary>
    /// Просит модель назвать участников и печатает, какая метка чьим именем стала
    /// и сколько реплик это затронуло, плюс разбор голосования.
    /// </summary>
    public static async Task Names(
        string logPath, string transcriptPath, string model, Services.LanguageProfile language)
    {
        try
        {
            var entries = LocalData.LoadTranscript(transcriptPath);
            var labels = entries.Select(x => x.Speaker).Distinct().OrderBy(x => x).ToList();

            using var ollama = new Services.OllamaClient();
            var trace = new List<string>();
            var names = await Services.SpeakerNamer.DetectAsync(ollama, model, entries, language,
                trace: trace, ownerName: LocalData.Setting("ownerName"));

            var log = new System.Text.StringBuilder();
            log.AppendLine(names.Count > 0 ? "OK" : "FAIL");
            log.AppendLine($"файл: {transcriptPath}");
            log.AppendLine($"реплик: {entries.Count}, меток: {labels.Count}, распознано имён: {names.Count}");
            log.AppendLine();
            foreach (var label in labels)
            {
                int count = entries.Count(x => x.Speaker == label);
                log.AppendLine(names.TryGetValue(label, out var name)
                    ? $"{label} ({count} реплик) → {name}"
                    : $"{label} ({count} реплик) → —");
            }
            log.AppendLine();
            log.AppendLine("--- РАЗБОР ---");
            foreach (var t in trace) log.AppendLine(t);

            File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL{Environment.NewLine}{ex}{Environment.NewLine}");
        }
    }

    /// <summary>LLM-коррекция реплик, испорченных так, как их обычно портит ASR языка записи.</summary>
    public static async Task Fix(string logPath, string model, Services.LanguageProfile? language = null)
    {
        try
        {
            var profile = language ?? Services.Languages.Russian;
            bool english = profile.Language == Services.TranscriptionLanguage.English;

            // Реплики в том виде, в каком их обычно портит ASR соответствующего языка
            var raw = english
                ? new List<TranscriptEntry>
                {
                    new("Me", "we need to deploy the feature to prod via github actions before the dead line",
                        TimeSpan.FromSeconds(5)),
                    new("Speaker", "i will review the pull request and spin up the kubernetes cluster",
                        TimeSpan.FromSeconds(15)),
                    new("Me", "the api returns a five hundred when the backend does not respond",
                        TimeSpan.FromSeconds(25)),
                    new("Speaker", "lets discuss it at the next sprint review", TimeSpan.FromSeconds(35)),
                }
                : new List<TranscriptEntry>
                {
                    new("Я", "надо задеплоить фичу на прод через гитхаб экшенс до дед лайна", TimeSpan.FromSeconds(5)),
                    new("Собеседник", "я посмотрю пул реквест и подниму кубернетес кластер", TimeSpan.FromSeconds(15)),
                    new("Я", "эйпиай отдаёт пятисотку когда бэкенд не отвечает", TimeSpan.FromSeconds(25)),
                    new("Собеседник", "давай обсудим на следующем спринт ревью", TimeSpan.FromSeconds(35)),
                };

            using var ollama = new Services.OllamaClient();
            var fixedEntries = await Services.TranscriptCorrector.CorrectAsync(
                ollama, model, raw, "GitHub Actions, Kubernetes, API, deadline, pull request, sprint review",
                progress: null, language: profile);

            var sb = new System.Text.StringBuilder("OK\n--- БЫЛО ---\n");
            foreach (var en in raw) sb.AppendLine(en.ToString());
            sb.AppendLine("--- СТАЛО ---");
            foreach (var en in fixedEntries) sb.AppendLine(en.ToString());
            File.WriteAllText(logPath, sb.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    /// <summary>Итоги по короткому транскрипту: глоссарий должен дойти до итогов.</summary>
    public static async Task Summary(string logPath, string model)
    {
        try
        {
            // Названия в репликах намеренно искажены так, как их ломает распознавание
            // («Firebace», «Крейзи Геймс»): проверяем, что глоссарий доходит до итогов
            // и модель пишет канонические формы, а не разнобой из транскрипта.
            var transcript = new List<TranscriptEntry>
            {
                new("Я", "Коллеги, предлагаю перенести релиз на пятницу, нужно закрыть баг с оплатой.", TimeSpan.FromSeconds(5)),
                new("Собеседник", "Согласен. Тогда я подготовлю тестовый стенд к четвергу и проверю Firebace.", TimeSpan.FromSeconds(15)),
                new("Я", "Хорошо, а я до среды допишу интеграцию с платёжным шлюзом и выложу билд на Крейзи Геймс.", TimeSpan.FromSeconds(25)),
                new("Собеседник", "Остаётся открытым вопрос по дизайну главного экрана — обсудим на следующей встрече.", TimeSpan.FromSeconds(40)),
            };
            const string glossary = "Firebase, Crazy Games";

            using var ollama = new Services.OllamaClient();
            var sb = new System.Text.StringBuilder();
            var outcome = new Services.ChatOutcome();
            await foreach (var chunk in Services.SummaryComposer.ComposeAsync(
                ollama, model, transcript, stage: null, outcome, glossary: glossary))
            {
                sb.Append(chunk);
            }

            var answer = sb.ToString();
            var terms = new[] { "Firebase", "Crazy Games" };
            var restored = terms.Where(t => answer.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            var status = restored.Count == terms.Length ? "OK" : "FAIL";
            File.WriteAllText(logPath,
                $"{status}\ndone_reason={outcome.DoneReason}\n" +
                $"глоссарий: восстановлено {restored.Count} из {terms.Length} ({string.Join(", ", restored)})\n{answer}",
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    /// <summary>
    /// Итоги по транскрипту длинной встречи. Час разговора — это ~40 000 токенов, которые
    /// в окно локальной модели не влезают: раньше Ollama урезал промпт и ответ обрывался
    /// на нескольких строках. Проверяем, что все пять разделов итогов на месте.
    /// </summary>
    public static async Task SummaryLong(string logPath, string model, int minutes,
        Services.LanguageProfile? language = null)
    {
        try
        {
            var profile = language ?? Services.Languages.Russian;
            var transcript = SyntheticTranscript(minutes, profile);
            var text = string.Join("\n", transcript);

            using var ollama = new Services.OllamaClient();
            var sb = new System.Text.StringBuilder();
            var outcome = new Services.ChatOutcome();
            var stages = new List<string>();
            var stage = new Progress<string>(stages.Add);

            var started = DateTime.Now;
            await foreach (var chunk in Services.SummaryComposer.ComposeAsync(
                ollama, model, transcript, stage, outcome, profile))
            {
                sb.Append(chunk);
            }
            var answer = sb.ToString();

            var sections = Services.SummaryPrompt.SectionsFor(profile);
            var missing = new List<string>();
            foreach (var section in sections)
                if (!answer.Contains(section, StringComparison.Ordinal)) missing.Add(section);

            bool ok = missing.Count == 0 && !outcome.HitContextLimit;
            var log = new System.Text.StringBuilder(ok ? "OK\n" : "FAIL\n");
            log.AppendLine($"язык={profile.DisplayName} минут={minutes} реплик={transcript.Count} " +
                           $"токенов_на_входе≈{Services.OllamaClient.EstimateTokens(text)}");
            log.AppendLine($"done_reason={outcome.DoneReason} длина_ответа={answer.Length} " +
                           $"время={(DateTime.Now - started).TotalSeconds:F0}с");
            log.AppendLine($"нет_разделов: {(missing.Count == 0 ? "—" : string.Join(", ", missing))}");
            if (stages.Count > 0) log.AppendLine($"этапы: {string.Join(" | ", stages)}");
            log.AppendLine("--- ИТОГИ ---").AppendLine(answer);
            File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    /// <summary>Синтетический транскрипт заданной длительности: реплика раз в ~8 секунд.</summary>
    private static List<TranscriptEntry> SyntheticTranscript(int minutes, Services.LanguageProfile profile)
    {
        bool english = profile.Language == Services.TranscriptionLanguage.English;

        string[] mineEn =
        [
            "Let's start with the status of the payment gateway integration.",
            "I closed the authorisation task yesterday, the tests are still pending.",
            "I suggest moving this logic into a separate service, it will be easier to maintain.",
            "We are on track for Friday unless something else comes up.",
            "Let's agree the report goes out before the end of the month.",
            "There was a bug with timeouts, I fixed it but it needs a review.",
        ];
        string[] theirsEn =
        [
            "Agreed, but we need to decide who owns the database migration.",
            "I will look at the load and come back with numbers tomorrow.",
            "The client asked for a spreadsheet export, that is a big piece of work.",
            "I will bring the staging environment up by Thursday, it is missing access rights.",
            "Let's move the design question to the next meeting.",
            "Production went down twice this week, we need to find the root cause.",
        ];

        string[] mine =
        [
            "Давайте начнём со статуса по интеграции с платёжным шлюзом.",
            "Я вчера закрыл задачу по авторизации, осталось прогнать тесты.",
            "Предлагаю вынести эту логику в отдельный сервис, поддерживать будет проще.",
            "По срокам мы успеваем к пятнице, если не всплывёт что-то ещё.",
            "Зафиксируем: отчёт готовим до конца месяца.",
            "Тут был баг с таймаутами, я его починил, нужно ревью.",
        ];
        string[] theirs =
        [
            "Согласен, но нужно понять, кто отвечает за миграцию базы.",
            "Я посмотрю на нагрузку и вернусь с цифрами завтра.",
            "Клиент просил добавить выгрузку в таблицу, это большая работа.",
            "Тестовый стенд подниму к четвергу, там не хватает доступов.",
            "Давайте вынесем вопрос по дизайну на следующую встречу.",
            "У нас упало на проде дважды за неделю, надо разобраться с причиной.",
        ];

        var entries = new List<TranscriptEntry>();
        var random = new Random(42);
        for (int i = 0, count = minutes * 60 / 8; i < count; i++)
        {
            var speaker = i % 3 == 0 ? profile.MeLabel
                        : i % 3 == 1 ? profile.NumberedOther(1)
                                     : profile.NumberedOther(2);
            bool isMine = speaker == profile.MeLabel;
            var pool = english
                ? (isMine ? mineEn : theirsEn)
                : (isMine ? mine : theirs);
            entries.Add(new TranscriptEntry(
                speaker, pool[random.Next(pool.Length)],
                TimeSpan.FromSeconds(i * 8), TimeSpan.FromSeconds(7)));
        }
        return entries;
    }
}
