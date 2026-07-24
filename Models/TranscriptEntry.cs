using System;

namespace CallAudioRecorder.Models;

/// <summary>Одна реплика транскрипта.</summary>
public sealed record TranscriptEntry(string Speaker, string Text, TimeSpan StartTime, TimeSpan Duration = default)
{
    /// <summary>Реплика с моего микрофона (иначе — системный звук, собеседник).</summary>
    public bool IsMe => Speaker == "Я";

    /// <summary>Момент окончания реплики.</summary>
    public TimeSpan End => StartTime + Duration;

    public string TimeLabel => StartTime.ToString(@"hh\:mm\:ss");

    /// <summary>Склеивает со следующей репликой того же спикера.</summary>
    public TranscriptEntry MergeWith(TranscriptEntry next) =>
        this with { Text = $"{Text} {next.Text}", Duration = next.End - StartTime };

    public override string ToString() => $"[{TimeLabel}] {Speaker}: {Text}";
}
