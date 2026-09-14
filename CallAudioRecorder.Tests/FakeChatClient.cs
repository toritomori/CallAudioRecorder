using System.Runtime.CompilerServices;
using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Подменная модель: отдаёт заготовленные ответы вместо Ollama. Нужна, чтобы проверять логику
/// вокруг LLM — разбиение на части, отказ при неуверенности, разбор ответа не по формату, —
/// не завися от того, что именно сегодня скажет живая модель.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<Call, string> _respond;

    /// <summary>Один запрос к модели — всё, что сервис ей передал.</summary>
    public sealed record Call(string Model, string System, string User, int ResponseTokens, double Temperature);

    /// <summary>Все запросы по порядку: по ним видно, сколько частей ушло и что было в промпте.</summary>
    public List<Call> Calls { get; } = [];

    /// <summary>Сколько токенов входа «вмещает» модель. int.MaxValue — как у облачной.</summary>
    public int InputBudget { get; init; } = 30_000;

    /// <summary>Чем закончилась генерация: «length» имитирует обрыв по окну контекста.</summary>
    public string DoneReason { get; init; } = "stop";

    public FakeChatClient(Func<Call, string> respond) => _respond = respond;

    /// <summary>Ответы по очереди; когда заготовки кончились, повторяется последняя.</summary>
    public FakeChatClient(params string[] answers)
    {
        int next = 0;
        _respond = _ => answers[Math.Min(next++, answers.Length - 1)];
    }

    public Task<int> GetInputBudgetAsync(string model, int responseTokens, CancellationToken ct = default) =>
        Task.FromResult(InputBudget == int.MaxValue ? int.MaxValue : InputBudget - responseTokens);

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string model, string systemPrompt, string userMessage,
        int responseTokens = 2048, ChatOutcome? outcome = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        double temperature = IChatClient.DefaultTemperature)
    {
        var call = new Call(model, systemPrompt, userMessage, responseTokens, temperature);
        Calls.Add(call);
        var answer = _respond(call);

        // Стрим кусками, как у Ollama: потребитель не должен рассчитывать на ответ одним фрагментом.
        for (int i = 0; i < answer.Length; i += 7)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return answer.Substring(i, Math.Min(7, answer.Length - i));
        }
        if (outcome is not null) outcome.DoneReason = DoneReason;
    }

    public async Task<string> ChatAsync(
        string model, string systemPrompt, string userMessage,
        int responseTokens = 2048, ChatOutcome? outcome = null, CancellationToken ct = default,
        double temperature = IChatClient.DefaultTemperature)
    {
        var parts = new List<string>();
        await foreach (var chunk in ChatStreamAsync(model, systemPrompt, userMessage, responseTokens,
                           outcome, ct, temperature))
            parts.Add(chunk);
        return string.Concat(parts);
    }
}
