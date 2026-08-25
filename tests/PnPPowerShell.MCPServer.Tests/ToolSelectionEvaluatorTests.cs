using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Gates tool descriptions on whether a plausible prompt selects the right tool.</summary>
// Scores choice, not command correctness.
public class ToolSelectionEvaluatorTests(ITestOutputHelper output)
{
    /// <summary>How often BM25 must match the model's pick.</summary>
    // Measured at 93 %. Below this, replace the scorer.
    private const double MinimumAgreement = 0.90;

    public static TheoryData<string, string> Prompts()
    {
        var data = new TheoryData<string, string>();
        foreach (var (tool, prompt) in E2ETestPrompts.All)
        {
            data.Add(tool, prompt);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Prompts))]
    public void Every_prompt_ranks_its_tool_in_the_top_three(string expected, string prompt)
    {
        var selection = ToolSelectionEvaluator.Evaluate(prompt, expected);

        Assert.True(
            selection.InTopThree,
            $"'{prompt}' ranked {expected} at {(selection.Rank == 0 ? "no position" : selection.Rank.ToString())}. " +
            $"Top 3: {string.Join(", ", selection.Ranked.Take(3))}. " +
            "Fix the tool's [Description] rather than the prompt: the evaluator reads exactly what a client reads.");
    }

    [Fact]
    public void Every_tool_has_prompts()
    {
        var covered = E2ETestPrompts.All.Select(p => p.Tool).Distinct().ToHashSet(StringComparer.Ordinal);
        var missing = ToolCatalog.All.Select(t => t.ProtocolTool.Name).Where(n => !covered.Contains(n)).ToList();

        Assert.True(missing.Count == 0, $"No prompts in e2eTestPrompts.md for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Prompts_name_tools_that_exist()
    {
        var published = ToolCatalog.All.Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = E2ETestPrompts.All.Select(p => p.Tool).Distinct().Where(n => !published.Contains(n)).ToList();

        Assert.True(unknown.Count == 0, $"e2eTestPrompts.md names tools that do not exist: {string.Join(", ", unknown)}");
    }

    /// <summary>How often BM25's top pick matches a model reading the same descriptions.</summary>
    // The only test that checks the scorer itself.
    [Fact]
    public void Bm25_agrees_with_the_model_that_read_the_same_descriptions()
    {
        var judged = ModelSelections.All.Where(p => !p.Ambiguous).ToList();
        var disagreements = judged
            .Select(p => (p.Prompt, Model: p.Tool, Bm25: ToolSelectionEvaluator.Evaluate(p.Prompt, p.Tool).Ranked[0]))
            .Where(x => x.Model != x.Bm25)
            .ToList();

        var agreement = (double)(judged.Count - disagreements.Count) / judged.Count;

        output.WriteLine($"BM25 top-1 agrees with the model on {judged.Count - disagreements.Count}/{judged.Count} ({agreement:P0}).");
        foreach (var (prompt, model, bm25) in disagreements)
        {
            output.WriteLine($"  DIVERGES: '{prompt}' — model says {model}, BM25 says {bm25}");
        }

        Assert.True(
            agreement >= MinimumAgreement,
            $"BM25 matches the model's pick on only {agreement:P0} of prompts, below the recorded {MinimumAgreement:P0}. " +
            "The lexical scorer has stopped predicting how a model actually chooses.");
    }

    [Fact]
    public void The_model_labels_cover_exactly_the_maintained_prompts() =>
        Assert.Equal(
            E2ETestPrompts.All.Select(p => p.Prompt).Order(),
            ModelSelections.All.Select(p => p.Prompt).Order());
}

/// <summary>The maintained prompt list, read from e2eTestPrompts.md so the two cannot drift.</summary>
internal static class E2ETestPrompts
{
    public static readonly IReadOnlyList<(string Tool, string Prompt)> All = Load();

    private static List<(string, string)> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "e2eTestPrompts.md");
        var prompts = new List<(string, string)>();
        var tool = string.Empty;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                tool = line[3..].Trim();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) && tool.Length > 0)
            {
                prompts.Add((tool, line[2..].Trim()));
            }
        }

        return prompts;
    }
}

/// <summary>Tool picks made by a model reading only the published descriptions, for validating BM25.</summary>
internal static class ModelSelections
{
    public static readonly IReadOnlyList<(string Tool, string Prompt, bool Ambiguous)> All = Load();

    private static List<(string, string, bool)> Load()
    {
        var picks = new List<(string, string, bool)>();

        foreach (var raw in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "modelSelections.md")))
        {
            var line = raw.Trim();
            var split = line.IndexOf(" :: ", StringComparison.Ordinal);

            if (!line.StartsWith("- pnp_", StringComparison.Ordinal) || split < 0)
            {
                continue;
            }

            var prompt = line[(split + 4)..].Trim();
            var ambiguous = prompt.EndsWith("(ambiguous)", StringComparison.Ordinal);

            picks.Add((
                line[2..split].Trim(),
                ambiguous ? prompt[..^"(ambiguous)".Length].Trim() : prompt,
                ambiguous));
        }

        return picks;
    }
}
