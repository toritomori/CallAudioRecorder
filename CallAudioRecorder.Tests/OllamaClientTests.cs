using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Параметры запроса к Ollama. Резерв ответа — это и потолок генерации: без него зациклившаяся
/// модель пишет, пока не заполнит окно (на записи от 20.08 — 144 с вместо 10 на списке имён).
/// </summary>
public class OllamaClientTests
{
    [Fact]
    public void LocalModel_GetsWindowAndAnswerCap()
    {
        var options = OllamaClient.ChatOptions(isRemote: false, contextLimit: 262_144,
            "system", new string('а', 25_000), responseTokens: 256, temperature: 0);

        Assert.Equal(256, options["num_predict"]);
        Assert.Equal(12_288, options["num_ctx"]); // 3 + 10 001 + 256 = 10 260 → вверх до кратного 4096
    }

    [Fact]
    public void CloudModel_GetsAnswerCapButNoWindow()
    {
        // Окно облачной модели задаёт сервер: локальный num_ctx мог бы его только урезать.
        var options = OllamaClient.ChatOptions(isRemote: true, contextLimit: 196_608,
            "system", "текст", responseTokens: 4096, temperature: 0.3);

        Assert.Equal(4096, options["num_predict"]);
        Assert.False(options.ContainsKey("num_ctx"));
    }
}
