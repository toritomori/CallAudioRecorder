using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CallAudioRecorder.Services;

/// <summary>
/// Чат с языковой моделью — ровно то, чем пользуются сервисы итогов, коррекции и имён.
///
/// Зачем интерфейс: пока <see cref="SummaryComposer"/>, <see cref="SpeakerNamer"/> и
/// <see cref="TranscriptCorrector"/> принимали конкретный <see cref="OllamaClient"/>, их логику
/// (разбиение на части, отказ при неуверенности, разбор ответа не по формату) нельзя было
/// проверить без живого Ollama. Тестам хватает подставить заготовленные ответы модели.
/// </summary>
public interface IChatClient
{
    /// <summary>Температура по умолчанию: чуть живее нуля для текстовых ответов.</summary>
    const double DefaultTemperature = 0.3;

    /// <summary>
    /// Сколько токенов входа модель реально способна принять, если оставить responseTokens под ответ.
    /// int.MaxValue — окно задаёт сервер (облачная модель), резать вход не требуется.
    /// </summary>
    Task<int> GetInputBudgetAsync(string model, int responseTokens, CancellationToken ct = default);

    /// <summary>Стриминговый чат: отдаёт фрагменты текста ответа по мере генерации.</summary>
    IAsyncEnumerable<string> ChatStreamAsync(
        string model, string systemPrompt, string userMessage,
        int responseTokens = 2048, ChatOutcome? outcome = null,
        double temperature = DefaultTemperature, CancellationToken ct = default);

    /// <summary>Собирает ответ целиком — для промежуточных шагов, которые не показываются в UI.</summary>
    Task<string> ChatAsync(
        string model, string systemPrompt, string userMessage,
        int responseTokens = 2048, ChatOutcome? outcome = null,
        double temperature = DefaultTemperature, CancellationToken ct = default);
}
