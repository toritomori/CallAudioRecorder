using CallAudioRecorder.Models;
using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Живая лента (<see cref="TranscriptionService.AppendRecognized"/>) обязана совпасть с тем,
/// что окажется в файле после записи. Реплики приходят в порядке распознавания, а не речи:
/// каждый канал ждёт своего VAD, и длинная фраза собеседника встаёт в очередь позже короткой
/// реплики с микрофона, начавшейся после неё. Бывший режим <c>--order-test</c>.
/// </summary>
public class LiveFeedOrderTests
{
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 200).Select(i => new object[] { 20260908 + i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ShuffledRecognitionOrder_GivesSameFeedAsSortedTranscript(int seed)
    {
        var rnd = new Random(seed);

        // Реплики двух каналов вперемешку, с паузами меньше и больше порога склейки.
        var spoken = new List<TranscriptEntry>();
        var time = TimeSpan.Zero;
        for (int i = 0; i < 24; i++)
        {
            var speaker = rnd.Next(3) switch { 0 => "Я", 1 => "Собеседник 1", _ => "Собеседник 2" };
            var duration = TimeSpan.FromSeconds(rnd.Next(1, 12));
            spoken.Add(new TranscriptEntry(speaker, $"реплика {i}", time, duration));
            time += duration + TimeSpan.FromSeconds(rnd.Next(0, 6));
        }

        // Порядок распознавания: перемешиваем, но недалеко от исходного места.
        var recognized = spoken
            .Select((entry, i) => (entry, key: i + rnd.Next(0, 5)))
            .OrderBy(x => x.key)
            .Select(x => x.entry)
            .ToList();

        var flat = new List<TranscriptEntry>();
        var live = new List<TranscriptEntry>();
        foreach (var entry in recognized)
            live = TranscriptionService.AppendRecognized(flat, entry);

        var expected = TranscriptionService.MergeAdjacent(spoken.OrderBy(x => x.StartTime));

        Assert.Equal(Describe(expected), Describe(live));
    }

    [Fact]
    public void LateLongPhrase_StandsBeforeShortReplyThatStartedAfterIt()
    {
        // Случай из живой записи: собеседник говорил 00:29:38–00:29:50, а короткий ответ
        // с микрофона в 00:29:48 распознался раньше — лента показывала ответ выше вопроса.
        var question = new TranscriptEntry("Собеседник 1", "вопрос", Clock(29, 38), TimeSpan.FromSeconds(12));
        var answer = new TranscriptEntry("Я", "ответ", Clock(29, 48), TimeSpan.FromSeconds(1));

        var flat = new List<TranscriptEntry>();
        TranscriptionService.AppendRecognized(flat, answer);
        var live = TranscriptionService.AppendRecognized(flat, question);

        Assert.Equal(["Собеседник 1: вопрос", "Я: ответ"], live.Select(e => $"{e.Speaker}: {e.Text}"));
    }

    [Fact]
    public void LateEntry_SplitsBlockThatLookedContinuous()
    {
        // Две реплики «Я» с паузой меньше порога склеились в один блок — пока не пришла
        // опоздавшая реплика собеседника, сказанная между ними. Доклейка на месте этого
        // уже не отменила бы: текст соседей к тому моменту слит.
        var flat = new List<TranscriptEntry>();
        TranscriptionService.AppendRecognized(flat, new("Я", "раз", TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2)));
        var merged = TranscriptionService.AppendRecognized(flat,
            new("Я", "два", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)));
        Assert.Single(merged);

        var split = TranscriptionService.AppendRecognized(flat,
            new("Собеседник 1", "между", TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(1)));

        Assert.Equal(["Я: раз", "Собеседник 1: между", "Я: два"], split.Select(e => $"{e.Speaker}: {e.Text}"));
    }

    private static TimeSpan Clock(int minutes, int seconds) => new(0, minutes, seconds);

    private static List<string> Describe(IEnumerable<TranscriptEntry> feed) =>
        feed.Select(x => $"{x.TimeLabel} {x.Speaker}: {x.Text}").ToList();
}
