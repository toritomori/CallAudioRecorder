using System;
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
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://localhost:11434"),
        Timeout = TimeSpan.FromMinutes(30) // длинная генерация на CPU-фолбэке
    };

    /// <summary>Имена установленных моделей. Бросает OllamaUnavailableException, если Ollama не запущен.</summary>
    public async Task<string[]> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/api/tags", ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("models").EnumerateArray()
                .Where(m => !m.TryGetProperty("capabilities", out var caps) ||
                            caps.EnumerateArray().Any(c => c.GetString() == "completion"))
                .Select(m => m.GetProperty("name").GetString()!)
                .ToArray();
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            throw new OllamaUnavailableException(
                "Ollama не запущен. Запустите Ollama и повторите.", ex);
        }
    }

    /// <summary>
    /// Стриминговый чат: отдаёт фрагменты текста ответа по мере генерации.
    /// При 404 по модели бросает OllamaModelMissingException.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string model, string systemPrompt, string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
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
            options = new { num_ctx = 16384, temperature = 0.3 },
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
                yield break;
        }
    }

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

public sealed class OllamaUnavailableException(string message, Exception inner) : Exception(message, inner);
public sealed class OllamaModelMissingException(string message) : Exception(message);
