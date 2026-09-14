using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CallAudioRecorder.Models;

namespace CallAudioRecorder.Diagnostics;

/// <summary>Данные, с которыми работает и приложение: settings.json и сохранённые транскрипты.</summary>
internal static class LocalData
{
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CallAudioRecorder", "settings.json");

    /// <summary>Значение поля из settings.json — чтобы самотесты работали с теми же настройками, что и UI.</summary>
    public static string Setting(string name)
    {
        if (!File.Exists(SettingsPath)) return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Читает сохранённый `*.транскрипт.md` обратно в реплики.</summary>
    public static List<TranscriptEntry> LoadTranscript(string path)
    {
        var line = new Regex(@"^\*\*\[(\d\d):(\d\d):(\d\d)\]\s*(.+?):\*\*\s*(.*)$");
        var result = new List<TranscriptEntry>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var m = line.Match(raw.Trim());
            if (!m.Success) continue;
            var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value));
            result.Add(new TranscriptEntry(m.Groups[4].Value, m.Groups[5].Value, start));
        }
        return result;
    }
}
