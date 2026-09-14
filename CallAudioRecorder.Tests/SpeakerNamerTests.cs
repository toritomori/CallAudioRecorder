using CallAudioRecorder.Models;
using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Голосование за имена участников. Модель подменена: у неё спрашивается только список имён,
/// а кто есть кто — решается детерминированно, и именно это решение здесь закреплено.
/// Перекличка повторяет узоры планёрки от 14.09, на которой отлаживалось правило:
/// имя называют СЛЕДУЮЩЕМУ говорящему, а не тому, кто его произнёс.
/// </summary>
public class SpeakerNamerTests
{
    private const string Owner = "Антон";

    private static List<TranscriptEntry> Transcript(params (string Speaker, string Text)[] lines) =>
        lines.Select((l, i) => new TranscriptEntry(l.Speaker, l.Text, TimeSpan.FromSeconds(i * 20))).ToList();

    private static readonly List<TranscriptEntry> RollCall = Transcript(
        ("Я", "Всем привет, начинаем планёрку. Дальше, Дима."),
        ("Собеседник 1", "Вчера доделал экран магазина, сегодня баги по оплате. Ира."),
        ("Собеседник 2", "Я закрыла анимации, в пятницу был два. Передаю слово давай Людмиле."),
        ("Собеседник 3", "По рекламе всё стабильно, Андрей вчера прислал отчёт, надо посмотреть."),
        ("Собеседник 4", "Отчёт я видел, цифры нормальные."),
        ("Я", "Хорошо, спасибо. Передаю слово Артёму."),
        ("Собеседник 5", "У меня всё по плану."));

    private static Task<IReadOnlyDictionary<string, string>> Detect(
        FakeChatClient model, IReadOnlyList<TranscriptEntry> entries, string? owner = Owner) =>
        SpeakerNamer.DetectAsync(model, "fake", entries, Languages.Russian, ownerName: owner);

    [Fact]
    public async Task RollCall_GivesNameToNextSpeaker_NotToTheOneWhoSaidIt()
    {
        var model = new FakeChatClient("Дима, Ира, Людмила, Андрей, Артём, Антон");

        var names = await Detect(model, RollCall);

        // «Дальше, Дима.» говорит «Я», а Дима — следующий; «Артёму» узнаётся как «Артём».
        Assert.Equal("Дима", names["Собеседник 1"]);
        Assert.Equal("Ира", names["Собеседник 2"]);
        Assert.Equal("Людмила", names["Собеседник 3"]);
        Assert.Equal("Артём", names["Собеседник 5"]);
    }

    [Fact]
    public async Task MentionWithoutHandover_LeavesLabelAsIs()
    {
        // «Андрей вчера прислал отчёт» — разговор о человеке, а не передача слова: так на
        // реальной планёрке «Собеседник 6» стал Андреем, которого там не было.
        var names = await Detect(new FakeChatClient("Дима, Ира, Людмила, Андрей, Артём"), RollCall);

        Assert.False(names.ContainsKey("Собеседник 4"));
        Assert.DoesNotContain("Андрей", names.Values);
    }

    [Fact]
    public async Task OwnerLabel_IsNeverRenamed()
    {
        var names = await Detect(new FakeChatClient("Дима, Ира, Людмила, Артём"), RollCall);
        Assert.False(names.ContainsKey("Я"));
    }

    [Fact]
    public async Task TieBetweenTwoNames_LeavesLabelAsIs()
    {
        var entries = Transcript(
            ("Я", "Дальше, Олег."),
            ("Собеседник 1", "Готово."),
            ("Я", "Дальше, Паша."),
            ("Собеседник 1", "Тоже готово."));

        var names = await Detect(new FakeChatClient("Олег, Паша"), entries);

        Assert.Empty(names);
    }

    [Fact]
    public async Task TwoLabelsClaimingOneNameEqually_GetNeither()
    {
        var entries = Transcript(
            ("Я", "Дальше, Олег."),
            ("Собеседник 1", "Готово."),
            ("Я", "Дальше, Олег."),
            ("Собеседник 2", "У меня тоже."));

        var names = await Detect(new FakeChatClient("Олег"), entries);

        Assert.Empty(names);
    }

    [Fact]
    public async Task SelfIntroduction_NamesTheSpeaker()
    {
        var entries = Transcript(
            ("Собеседник 1", "Всем привет, я Марина, отвечаю за рекламу."),
            ("Я", "Привет."));

        var names = await Detect(new FakeChatClient("Марина"), entries);

        Assert.Equal("Марина", names["Собеседник 1"]);
    }

    [Fact]
    public async Task OwnerName_IsExcludedFromCandidates()
    {
        // К владельцу записи обращаются чаще всех; если его имя попадёт в кандидаты,
        // любое обращение к нему станет голосом за соседа.
        var entries = Transcript(
            ("Собеседник 1", "Дальше, Антон."),
            ("Собеседник 2", "Я за Антона скажу: всё готово."));

        var names = await Detect(new FakeChatClient("Антон"), entries);

        Assert.Empty(names);
    }

    [Fact]
    public async Task InventedAndRamblingNames_AreFilteredByTranscript()
    {
        // 9B-модель вместо списка иногда рассуждает вслух: берутся все слова с заглавной,
        // а отсев делает транскрипт — «Значит», «Возможно» и выдуманный Петя в нём не звучат.
        var model = new FakeChatClient("Значит, в разговоре звучат Дима и Ира. Возможно, ещё Петя.");

        var names = await Detect(model, RollCall);

        Assert.Equal(["Дима", "Ира"], names.Values.Order());
    }

    [Fact]
    public async Task WordsWrittenLowercaseMoreOften_AreNotNames()
    {
        // Зациклившаяся модель копирует фразы транскрипта, и «Вот» проходит проверку «написано
        // как имя»: распознавание ставит его отдельным предложением. На записи от 20.08 «Вот»
        // так отобрало метку у Вячеслава — а со строчной «вот» там написано в пять раз чаще.
        var entries = Transcript(
            ("Я", "Ну вот, начинаем. Дальше, Дима."),
            ("Собеседник 1", "Вот у меня всё по плану, вот такие дела. Вот."),
            ("Собеседник 2", "А у меня вот вопрос по билду."));

        var names = await Detect(new FakeChatClient("Вот, Дима, вот, Вот"), entries);

        Assert.Equal("Дима", names["Собеседник 1"]);
        Assert.False(names.ContainsKey("Собеседник 2"));
    }

    [Fact]
    public async Task LowercaseCheck_ComparesCaseForms()
    {
        // «Поке» со строчной как слово целиком не пишется, зато его форма «пока» — постоянно:
        // на записи от 20.08 оно так отобрало метку у Вячеслава. А «верно» — не форма «Веры»,
        // и настоящее имя из-за него отсеиваться не должно.
        var entries = Transcript(
            ("Я", "Ну пока всё, давайте дальше. Вера."),
            ("Собеседник 1", "У меня пока без новостей, всё верно. Поке."),
            ("Собеседник 2", "Пока не смотрел."));

        var names = await Detect(new FakeChatClient("Вера, Поке"), entries);

        Assert.Equal("Вера", names["Собеседник 1"]);
        Assert.False(names.ContainsKey("Собеседник 2"));
    }

    [Fact]
    public async Task TermsInForeignAlphabet_AreNotNames()
    {
        // Латиница в русском транскрипте — только канонизированные термины глоссария:
        // «Poki.» отдельным предложением иначе выглядит как передача слова.
        var entries = Transcript(
            ("Собеседник 1", "Билд уже выложили. Poki."),
            ("Собеседник 2", "Там всё висит, проверил."));

        var names = await Detect(new FakeChatClient("Poki"), entries);

        Assert.Empty(names);
    }

    [Fact]
    public async Task EnglishTranscript_KeepsLatinNames()
    {
        // Правило алфавита зеркально: в английской записи латинские имена — и есть имена.
        var entries = Transcript(
            ("Me", "Thanks everyone. Next, Mike."),
            ("Speaker 1", "All good on my side."));

        var names = await SpeakerNamer.DetectAsync(new FakeChatClient("Mike"), "fake", entries,
            Languages.English, ownerName: "Anton");

        Assert.Equal("Mike", names["Speaker 1"]);
    }

    [Fact]
    public async Task NamesAreAskedWithZeroTemperature_AndOwnerIsNotAskedWhenKnown()
    {
        var model = new FakeChatClient("Дима");

        await Detect(model, RollCall);

        var call = Assert.Single(model.Calls);
        Assert.Equal(0, call.Temperature);
    }

    [Fact]
    public async Task UnknownOwner_IsAskedSeparately_AndExcluded()
    {
        // Строке «Я = …» в общем ответе доверять нельзя, поэтому владелец — отдельный вопрос.
        var model = new FakeChatClient("Дима, Ира, Людмила, Артём", "Людмила");

        var names = await Detect(model, RollCall, owner: null);

        Assert.Equal(2, model.Calls.Count);
        Assert.Contains("ВЛАДЕЛЬЦА", model.Calls[1].System);
        Assert.DoesNotContain("Людмила", names.Values);
    }

    [Fact]
    public async Task NoNamesInAnswer_ChangesNothing()
    {
        var model = new FakeChatClient("?");

        var result = await SpeakerNamer.ApplyNamesAsync(model, "fake", RollCall, Languages.Russian, Owner);

        Assert.Same(RollCall, result);
    }

    [Fact]
    public async Task ApplyNames_ReplacesLabelsInEveryEntry()
    {
        var model = new FakeChatClient("Дима, Ира, Людмила, Артём");

        var result = await SpeakerNamer.ApplyNamesAsync(model, "fake", RollCall, Languages.Russian, Owner);

        Assert.Equal(["Я", "Дима", "Ира", "Людмила", "Собеседник 4", "Я", "Артём"], result.Select(e => e.Speaker));
    }
}
