using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CallAudioRecorder.Diagnostics;

/// <summary>Текст транскрипта без моделей: канонизация терминов и номеров версий на корпусе.</summary>
internal static class TextChecks
{
    /// <summary>
    /// Прогоняет <see cref="Services.TermCanonizer"/> по сохранённым транскриптам и пишет
    /// в лог все замены с частотой. Глоссарий берётся из settings.json — то есть проверяется
    /// ровно тот список, с которым работает приложение.
    /// </summary>
    public static void Canon(string logPath, string source)
    {
        try
        {
            string glossary = "";
            if (File.Exists(LocalData.SettingsPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(LocalData.SettingsPath));
                if (doc.RootElement.TryGetProperty("glossary", out var g)) glossary = g.GetString() ?? "";
            }

            var canonizer = Services.TermCanonizer.Build(glossary);
            if (canonizer is null)
            {
                File.WriteAllText(logPath, "FAIL\nВ глоссарии нет латинских терминов — канонизировать нечего.\n",
                    System.Text.Encoding.UTF8);
                return;
            }

            var files = Directory.Exists(source)
                // Имя файла следует языку интерфейса, в котором велась запись
                ? Directory.GetFiles(source, "*.транскрипт.md").Concat(Directory.GetFiles(source, "*.transcript.md"))
                    .ToArray()
                : new[] { source };

            var replacements = new List<(string From, string To)>();
            var versions = new List<(string From, string To)>();
            int lines = 0, changed = 0;

            foreach (var file in files)
            {
                var entries = LocalData.LoadTranscript(file);
                lines += entries.Count;
                foreach (var entry in entries)
                {
                    int before = replacements.Count;
                    canonizer.Apply(entry.Text, replacements);
                    if (replacements.Count > before) changed++;
                }
                // Версии считаются по записи целиком: семейство «1.8.x» подтверждается
                // в одном месте, а применяется ко всем «183» файла.
                Services.VersionNormalizer.Normalize(entries, versions);
            }

            var log = new System.Text.StringBuilder();
            log.AppendLine(replacements.Count > 0 ? "OK" : "FAIL");
            log.AppendLine($"файлов: {files.Length}, реплик: {lines}, с заменами: {changed}, замен: {replacements.Count}");
            log.AppendLine();
            log.AppendLine("--- ЗАМЕНЫ (частота, было → стало) ---");
            foreach (var group in replacements.GroupBy(r => $"{r.From} → {r.To}").OrderByDescending(g => g.Count()))
                log.AppendLine($"{group.Count(),4}  {group.Key}");

            log.AppendLine();
            log.AppendLine($"--- ВЕРСИИ (замен: {versions.Count}) ---");
            foreach (var group in versions.GroupBy(r => $"{r.From} → {r.To}").OrderByDescending(g => g.Count()))
                log.AppendLine($"{group.Count(),4}  {group.Key}");

            File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }
}
