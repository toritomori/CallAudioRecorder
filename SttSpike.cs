using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace CallAudioRecorder;

/// <summary>
/// Спайк производительности: распознаёт WAV моделью выбранного языка (GigaAM v3 или
/// Parakeet TDT) и меряет RTF (отношение времени распознавания к длительности аудио)
/// при разном числе потоков. Заодно показывает распознанный текст — по нему видно,
/// что модель вообще та, что нужна.
/// </summary>
public static class SttSpike
{
    public static void Run(string logPath, string wavPath, Services.LanguageProfile? language = null)
    {
        var log = new StringBuilder();
        try
        {
            var profile = language ?? Services.Languages.Russian;
            log.AppendLine($"язык={profile.DisplayName} модель={profile.ModelDir}");

            // WAV → 16 kHz mono float
            float[] samples = LoadMono16k(wavPath);
            double audioSeconds = samples.Length / 16000.0;
            log.AppendLine($"wav={wavPath} duration={audioSeconds:F2}s samples={samples.Length}");

            foreach (int threads in new[] { 1, 2, 3 })
            {
                var config = new OfflineRecognizerConfig();
                config.FeatConfig.SampleRate = 16000;
                config.ModelConfig.Transducer.Encoder = profile.ModelPath(profile.EncoderFile);
                config.ModelConfig.Transducer.Decoder = profile.ModelPath(profile.DecoderFile);
                config.ModelConfig.Transducer.Joiner = profile.ModelPath(profile.JoinerFile);
                config.ModelConfig.Tokens = profile.ModelPath("tokens.txt");
                config.ModelConfig.ModelType = "nemo_transducer";
                config.ModelConfig.NumThreads = threads;
                config.DecodingMethod = "greedy_search";

                using var recognizer = new OfflineRecognizer(config);

                // прогрев + замер (первый прогон всегда медленнее)
                for (int pass = 0; pass < 2; pass++)
                {
                    var sw = Stopwatch.StartNew();
                    using var stream = recognizer.CreateStream();
                    stream.AcceptWaveform(16000, samples);
                    recognizer.Decode(stream);
                    sw.Stop();
                    string text = stream.Result.Text;
                    double rtf = sw.Elapsed.TotalSeconds / audioSeconds;
                    log.AppendLine($"threads={threads} pass={pass} time={sw.Elapsed.TotalSeconds:F2}s RTF={rtf:F3}");
                    if (pass == 1 && threads == 2)
                        log.AppendLine($"TEXT: {text}");
                }
            }

            // Боевой конвейер использует modified_beam_search (без него не работают hotwords) —
            // проверяем, что модель этого языка его вообще принимает.
            {
                var config = new OfflineRecognizerConfig();
                config.FeatConfig.SampleRate = 16000;
                config.ModelConfig.Transducer.Encoder = profile.ModelPath(profile.EncoderFile);
                config.ModelConfig.Transducer.Decoder = profile.ModelPath(profile.DecoderFile);
                config.ModelConfig.Transducer.Joiner = profile.ModelPath(profile.JoinerFile);
                config.ModelConfig.Tokens = profile.ModelPath("tokens.txt");
                config.ModelConfig.ModelType = "nemo_transducer";
                config.ModelConfig.NumThreads = 2;
                config.DecodingMethod = "modified_beam_search";
                config.MaxActivePaths = 4;

                using var recognizer = new OfflineRecognizer(config);
                var sw = Stopwatch.StartNew();
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(16000, samples);
                recognizer.Decode(stream);
                sw.Stop();
                log.AppendLine($"modified_beam_search: time={sw.Elapsed.TotalSeconds:F2}s " +
                               $"RTF={sw.Elapsed.TotalSeconds / audioSeconds:F3}");
                log.AppendLine($"BEAM TEXT: {stream.Result.Text}");
            }

            log.Insert(0, "OK\n");
        }
        catch (Exception ex)
        {
            log.AppendLine($"FAIL\n{ex}");
        }
        File.WriteAllText(logPath, log.ToString());
    }

    /// <summary>Читает аудиофайл и приводит к 16 kHz mono float.</summary>
    public static float[] LoadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider sp = reader;
        if (sp.WaveFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sp.WaveFormat.SampleRate != 16000)
            sp = new WdlResamplingSampleProvider(sp, 16000);

        var all = new System.Collections.Generic.List<float>(16000 * 60);
        var buf = new float[16000];
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) all.Add(buf[i]);
        return all.ToArray();
    }
}
