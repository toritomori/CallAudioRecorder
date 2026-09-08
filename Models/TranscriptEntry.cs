using System;

namespace CallAudioRecorder.Models;

/// <summary>Одна реплика транскрипта.</summary>
public sealed record TranscriptEntry(string Speaker, string Text, TimeSpan StartTime, TimeSpan Duration = default)
{
    /// <summary>
    /// Что выдал распознаватель до канонизации терминов — заполняется, только если
    /// <see cref="Services.TermCanonizer"/> что-то изменил. Нужен, чтобы правку можно было
    /// проверить и откатить: подстановка по глоссарию перезаписывает текст реплики.
    /// </summary>
    public string? RawText { get; init; }

    /// <summary>Реплика с моего микрофона (иначе — системный звук, собеседник).</summary>
    public bool IsMe => Services.Languages.IsMeLabel(Speaker);

    /// <summary>Момент окончания реплики.</summary>
    public TimeSpan End => StartTime + Duration;

    public string TimeLabel => StartTime.ToString(@"hh\:mm\:ss");

    /// <summary>Склеивает со следующей репликой того же спикера.</summary>
    public TranscriptEntry MergeWith(TranscriptEntry next) => this with
    {
        Text = $"{Text} {next.Text}",
        // Исходный текст склеиваем только если он вообще есть хоть у одной из реплик,
        // иначе поле так и остаётся пустым — «канонизация ничего не трогала».
        RawText = RawText is null && next.RawText is null
            ? null
            : $"{RawText ?? Text} {next.RawText ?? next.Text}",
        Duration = next.End - StartTime,
    };

    public override string ToString() => $"[{TimeLabel}] {Speaker}: {Text}";
}
