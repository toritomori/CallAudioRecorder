using System;
using System.Collections.Generic;
using System.IO;

namespace CallAudioRecorder.Services;

/// <summary>Язык распознавания. Выбирается до старта записи — модель грузится под него.</summary>
public enum TranscriptionLanguage
{
    Russian,
    English
}

/// <summary>
/// Всё, что зависит от языка распознавания: модель, метки спикеров, фильтр глоссария.
///
/// Русский и английский принципиально не сводятся к одной модели: словарь GigaAM почти
/// не содержит латиницы, а мультиязычные модели заметно проигрывают ей на русском.
/// Поэтому на каждый язык — своя специализированная модель, и обе дают пунктуацию
/// и заглавные буквы сразу.
/// </summary>
public sealed record LanguageProfile
{
    public required TranscriptionLanguage Language { get; init; }

    /// <summary>Подпись в выпадающем списке UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Каталог модели внутри ModelsRoot.</summary>
    public required string ModelDir { get; init; }

    public required string EncoderFile { get; init; }
    public required string DecoderFile { get; init; }
    public required string JoinerFile { get; init; }

    /// <summary>Метка моего канала (микрофон). От неё зависит <c>TranscriptEntry.IsMe</c>.</summary>
    public required string MeLabel { get; init; }

    /// <summary>Метка канала собеседника; при диаризации к ней добавляется номер.</summary>
    public required string OtherLabel { get; init; }

    /// <summary>Слово «Собеседник N» → как называется пронумерованный спикер.</summary>
    public string NumberedOther(int index) => $"{OtherLabel} {index}";

    /// <summary>Годится ли слово глоссария как hotword для этой модели (алфавит должен совпадать).</summary>
    public required Func<string, bool> HotwordFilter { get; init; }

    public string ModelPath(string file) =>
        Path.Combine(TranscriptionService.ModelsRoot, ModelDir, file);

    public override string ToString() => DisplayName;
}

/// <summary>Профили поддерживаемых языков.</summary>
public static class Languages
{
    /// <summary>GigaAM v3 — transducer с пунктуацией, специализированный русский.</summary>
    public static readonly LanguageProfile Russian = new()
    {
        Language = TranscriptionLanguage.Russian,
        DisplayName = "Русский",
        ModelDir = "giga-am-v3-punct",
        EncoderFile = "encoder.int8.onnx",
        DecoderFile = "decoder.onnx",
        JoinerFile = "joiner.onnx",
        MeLabel = "Я",
        OtherLabel = "Собеседник",
        // Латиница для GigaAM бесполезна: её почти нет в словаре (85 токенов из 1025).
        HotwordFilter = word => System.Text.RegularExpressions.Regex.IsMatch(word, @"^[а-яёА-ЯЁ\s-]+$"),
    };

    /// <summary>Parakeet TDT 0.6B v2 — английский transducer NeMo, тоже с пунктуацией и заглавными.</summary>
    public static readonly LanguageProfile English = new()
    {
        Language = TranscriptionLanguage.English,
        DisplayName = "English",
        ModelDir = "parakeet-tdt-0.6b-v2",
        EncoderFile = "encoder.int8.onnx",
        DecoderFile = "decoder.int8.onnx",
        JoinerFile = "joiner.int8.onnx",
        MeLabel = "Me",
        OtherLabel = "Speaker",
        // Зеркально: кириллица для английской модели бесполезна.
        HotwordFilter = word => System.Text.RegularExpressions.Regex.IsMatch(word, @"^[a-zA-Z0-9\s.'-]+$"),
    };

    public static IReadOnlyList<LanguageProfile> All { get; } = [Russian, English];

    public static LanguageProfile Get(TranscriptionLanguage language) =>
        language == TranscriptionLanguage.English ? English : Russian;

    /// <summary>Разбор значения из settings.json; неизвестное — русский.</summary>
    public static LanguageProfile Parse(string? name) =>
        string.Equals(name, "English", StringComparison.OrdinalIgnoreCase) ? English : Russian;

    /// <summary>Все метки «моего» канала — по ним <c>TranscriptEntry.IsMe</c> узнаёт себя.</summary>
    public static bool IsMeLabel(string speaker) =>
        speaker == Russian.MeLabel || speaker == English.MeLabel;
}
