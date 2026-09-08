using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CallAudioRecorder.Services;

/// <summary>Тонкий клиент локального Ollama (http://localhost:11434).</summary>
public sealed class OllamaClient : IDisposable
{
    /// <summary>
    /// Символов текста на один токен. Замерено на русском транскрипте (GigaAM → Qwen3.5):
    /// 115 425 символов = 41 810 токенов, то есть ~2.76. Берём 2.5 — оценка с запасом вверх.
    /// </summary>
    private const double CharsPerToken = 2.5;

    /// <summary>
    /// Потолок окна для локальных моделей. Ollama выделяет KV-кэш сразу на всё окно, поэтому
    /// просить у 4B-модели её паспортные 262 144 токена — верный способ выпасть из видеопамяти.
    /// 64k хватает примерно на два часа разговора; длиннее — через SummaryComposer по частям.
    /// </summary>
    public const int LocalContextCap = 65536;

    /// <summary>Меньше просить нет смысла: Ollama и сам поднимет окно до своего минимума.</summary>
    private const int MinContext = 8192;

    /// <summary>Температура по умолчанию: чуть живее нуля для текстовых ответов.</summary>
    private const double DefaultTemperature = 0.3;

    /// <summary>Окно для модели, о которой Ollama ничего не сообщил.</summary>
    private const int UnknownContextLimit = 32768;

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://localhost:11434"),
        Timeout = TimeSpan.FromMinutes(30) // длинная генерация на CPU-фолбэке
    };

    private readonly ConcurrentDictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _modelsLoaded;

    /// <summary>Что известно о модели: размер её окна и живёт ли она в облаке Ollama.</summary>
    private sealed record ModelInfo(int ContextLimit, bool IsRemote);

    /// <summary>Грубая оценка длины текста в токенах (в большую сторону).</summary>
    public static int EstimateTokens(string text) => (int)(text.Length / CharsPerToken) + 1;

    /// <summary>Имена установленных моделей. Бросает OllamaUnavailableException, если Ollama не запущен.</summary>
    public async Task<string[]> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/api/tags", ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

            var names = new List<string>();
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                if (m.GetProperty("name").GetString() is not { } name) continue;
                _models[name] = ReadModelInfo(m);

                if (m.TryGetProperty("capabilities", out var caps) &&
                    !caps.EnumerateArray().Any(c => c.GetString() == "completion")) continue;
                names.Add(name);
            }
            _modelsLoaded = true;
            return names.ToArray();
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            throw new OllamaUnavailableException(
                "Ollama не запущен. Запустите Ollama и повторите.", ex);
        }
    }

    private static ModelInfo ReadModelInfo(JsonElement model)
    {
        // Облачные модели помечены remote_host/remote_model: их окно задаёт сервер Ollama,
        // и num_ctx для них слать не нужно — можно только урезать штатные сотни тысяч токенов.
        bool remote = model.TryGetProperty("remote_host", out _) || model.TryGetProperty("remote_model", out _);

        int limit = UnknownContextLimit;
        if (model.TryGetProperty("details", out var details) &&
            details.TryGetProperty("context_length", out var ctx) &&
            ctx.TryGetInt32(out int value) && value > 0)
            limit = value;

        return new ModelInfo(limit, remote);
    }

    private async Task<ModelInfo> GetModelInfoAsync(string model, CancellationToken ct)
    {
        if (_models.TryGetValue(model, out var known)) return known;
        if (!_modelsLoaded)
        {
            try { await ListModelsAsync(ct); }
            catch (OllamaUnavailableException) { /* пусть об этом расскажет сам запрос генерации */ }
            if (_models.TryGetValue(model, out var loaded)) return loaded;
        }
        return new ModelInfo(UnknownContextLimit, IsRemote: false);
    }

    /// <summary>
    /// Сколько токенов входа модель реально способна принять, если оставить responseTokens под ответ.
    /// Для облачных моделей — int.MaxValue: их окно задаёт сервер, резать вход не требуется.
    /// </summary>
    public async Task<int> GetInputBudgetAsync(string model, int responseTokens, CancellationToken ct = default)
    {
        var info = await GetModelInfoAsync(model, ct);
        if (info.IsRemote) return int.MaxValue;
        return Math.Max(MinContext, Math.Min(LocalContextCap, info.ContextLimit)) - responseTokens;
    }

    /// <summary>
    /// Стриминговый чат: отдаёт фрагменты текста ответа по мере генерации.
    /// При 404 по модели бросает OllamaModelMissingException.
    /// </summary>
    /// <param name="responseTokens">
    /// Сколько токенов зарезервировать под ответ. Окно Ollama делится между промптом и генерацией:
    /// если промпт съел его целиком, модель обрывается на нескольких строках (done_reason = "length"),
    /// а промпт длиннее окна Ollama молча урезает — вместе с системной инструкцией.
    /// </param>
    /// <param name="outcome">Необязательная «расписка»: причина остановки генерации.</param>
    /// <param name="temperature">
    /// Разметочным задачам (кто есть кто) нужен воспроизводимый ответ, а не разнообразие:
    /// на 0.3 модель называла спикеров по-разному от прогона к прогону.
    /// </param>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string model, string systemPrompt, string userMessage,
        int responseTokens = 2048, ChatOutcome? outcome = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        double temperature = DefaultTemperature)
    {
        var options = new Dictionary<string, object> { ["temperature"] = temperature };
        var info = await GetModelInfoAsync(model, ct);
        if (!info.IsRemote)
        {
            int need = EstimateTokens(systemPrompt) + EstimateTokens(userMessage) + responseTokens;
            int cap = Math.Max(MinContext, Math.Min(LocalContextCap, info.ContextLimit));
            options["num_ctx"] = Math.Clamp(RoundUpTo(need, 4096), MinContext, cap);
        }

        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            stream = true,
            think = false,
            options,
            keep_alive = "10m"
        };

        using var response = await SendChatAsync(payload, model, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"Ollama: {err.GetString()}");
            if (doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content))
            {
                var chunk = content.GetString();
                if (!string.IsNullOrEmpty(chunk)) yield return chunk;
            }
            if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                if (outcome is not null && doc.RootElement.TryGetProperty("done_reason", out var reason))
                    outcome.DoneReason = reason.GetString();
                yield break;
            }
        }
    }

    /// <summary>Собирает ответ целиком — для промежуточных шагов, которые не показываются в UI.</summary>
    public async Task<string> ChatAsync(
        string model, string systemPrompt, string userMessage,
        int responseTokens = 2048, ChatOutcome? outcome = null, CancellationToken ct = default,
        double temperature = DefaultTemperature)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in ChatStreamAsync(model, systemPrompt, userMessage, responseTokens,
                           outcome, ct, temperature))
            sb.Append(chunk);
        return sb.ToString();
    }

    private static int RoundUpTo(int value, int step) => (value + step - 1) / step * step;

    private async Task<HttpResponseMessage> SendChatAsync(object payload, string model, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            throw new OllamaUnavailableException("Ollama не запущен. Запустите Ollama и повторите.", ex);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new OllamaModelMissingException(
                $"Модель «{model}» не установлена. Выполните в терминале: ollama pull {model}");
        }
        response.EnsureSuccessStatusCode();
        return response;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Чем закончилась генерация — заполняется по последнему кадру стрима.</summary>
public sealed class ChatOutcome
{
    /// <summary>«stop» — модель договорила, «length» — упёрлась в окно контекста.</summary>
    public string? DoneReason { get; internal set; }

    public bool HitContextLimit => DoneReason == "length";
}

public sealed class OllamaUnavailableException(string message, Exception inner) : Exception(message, inner);
public sealed class OllamaModelMissingException(string message) : Exception(message);
