using Xunit.Abstractions;

namespace StudyStash.Core.Tests;

/// <summary>
/// Against a real Ollama, only when asked: STUDYSTASH_LIVE_OLLAMA=qwen3:1.7b dotnet test. Checks the calls the
/// fakes stand in for elsewhere: notes, sorting, the length cap, and the model helpers.
/// </summary>
public class LiveOllamaTests(ITestOutputHelper output)
{
    static readonly string? Model = Environment.GetEnvironmentVariable("STUDYSTASH_LIVE_OLLAMA");

    const string Transcript = """
        Okay, let's get started. Today we're talking about cellular respiration, which is how cells turn glucose into
        usable energy in the form of ATP. Remember from last week that ATP is adenosine triphosphate. The whole process
        has three main stages. First, glycolysis, which happens in the cytoplasm and splits one glucose molecule into two
        pyruvate molecules. That gives a net gain of two ATP and two NADH. Second, the Krebs cycle, also called the citric
        acid cycle, which happens in the mitochondrial matrix. Each pyruvate is first converted to acetyl CoA, and then the
        cycle produces carbon dioxide, NADH, FADH2, and a little more ATP. Third, the electron transport chain on the inner
        mitochondrial membrane. This is where most of the ATP is made, around 32 to 34 molecules, through oxidative
        phosphorylation. Oxygen is the final electron acceptor, and it combines with hydrogen to form water. If there's no
        oxygen, cells can fall back on fermentation, which regenerates NAD+ so glycolysis can keep going, but it only makes
        two ATP per glucose. In your muscles that makes lactate; in yeast it makes ethanol and carbon dioxide. One way to
        remember the order is that glucose gets broken down step by step, and each step hands its high-energy electrons
        to a carrier, NADH or FADH2, which drops them off at the electron transport chain. That's why cyanide is so
        dangerous: it blocks the last enzyme in the chain, so no oxygen gets used and ATP production collapses. For Friday,
        read chapter nine, and the first problem set on metabolism is due next Wednesday at midnight. The midterm is on
        October twelfth and covers chapters six through nine. Any questions? Okay, see you Friday.
        """;

    [Fact]
    public async Task Notes_sorting_and_the_cap_on_a_real_model()
    {
        if (Model is null)
        {
            output.WriteLine("skipped: set STUDYSTASH_LIVE_OLLAMA to a model name");
            return;
        }
        var cfg = new Config(".", ".") { OllamaModel = Model, Classes = [new ClassDef("Bio 110", ["biology"]), new ClassDef("Calc 1")] };
        var models = await Ollama.ListModelsAsync(cfg.OllamaHost);
        Assert.NotNull(models);
        Assert.Contains(models, m => m.Name == Model);
        Assert.True(Ollama.HasModel(models.Select(m => m.Name).ToList(), Model));
        var (seconds, why) = await Ollama.TryModelAsync(cfg.OllamaHost, Model);
        Assert.True(seconds is not null, why);
        int? native = await Summarize.ModelContextAsync(cfg, Model);
        Assert.True(native > 0);

        var m = new Meeting("live") { Title = "Lecture 5", Date = "2026-09-24T09:00:00", Transcript = Transcript.ReplaceLineEndings(" ") };
        Assert.True(Summarize.WantsSummary(m, cfg));
        string notes = await Summarize.SummarizeTranscriptAsync(m, cfg);
        output.WriteLine(notes);
        Assert.Contains("##", notes);
        Assert.DoesNotContain("<think>", notes);

        var c = await Classify.ClassifyAsync(m.WithNotes(notes), cfg);
        output.WriteLine($"{c.ClassName} {c.By} {c.Confidence} {c.LectureTitle} [{string.Join(", ", c.Topics ?? [])}]");
        Assert.Equal("ollama", c.By);
        Assert.Contains(c.ClassName, new[] { "Bio 110", Configs.Unsorted });

        // A tiny context leaves room for 128 tokens: a thousand numbers can't fit, and that must fail, not loop.
        var e = await Assert.ThrowsAsync<RunawayOutputException>(() =>
            Summarize.OllamaGenerateAsync(cfg, Model, "Write every whole number from 1 to 1000, one per line, and nothing else.", 256));
        output.WriteLine(e.Message);
        Assert.Contains("kept writing past 128 tokens", e.Message);
    }
}
