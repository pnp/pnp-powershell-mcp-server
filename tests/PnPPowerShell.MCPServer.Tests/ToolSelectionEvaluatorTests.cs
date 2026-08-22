using System.Text;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Gates tool descriptions on whether a plausible prompt actually selects the right tool.</summary>
// Targets are microsoft/mcp's. A failure is a description missing the words a user would actually use.
public class ToolSelectionEvaluatorTests(ITestOutputHelper output)
{
    private const double MinimumConfidence = 0.4;

    /// <summary>The roadmap's target: this share of prompts at or above <see cref="MinimumConfidence"/>.</summary>
    private const double TargetRate = 0.95;

    /// <summary>How many prompts may sit below the bar. A ratchet — lower it when it improves, never raise it.</summary>
    // A count rather than a rate: a rate falls when you add a prompt, so ratcheting on one would fail the
    // build for adding the coverage Every_tool_has_prompts demands. A count only moves when a prompt the
    // descriptions genuinely do not serve is added, which is worth failing for.
    private const int MaxLowConfidencePrompts = 6;

    /// <summary>How often BM25 must match the model's pick for the scorer to be worth trusting.</summary>
    // Measured at 93 %. Below this the lexical proxy is no longer standing in for semantic selection,
    // and the honest response is to replace the scorer rather than to keep tuning descriptions against it.
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
    public void Confidence_does_not_regress()
    {
        var low = E2ETestPrompts.All
            .Select(p => ToolSelectionEvaluator.Evaluate(p.Prompt, p.Tool))
            .Where(s => s.Confidence < MinimumConfidence)
            .OrderBy(s => s.Confidence)
            .ToList();

        Assert.True(
            low.Count <= MaxLowConfidencePrompts,
            $"{low.Count} prompts sit below confidence {MinimumConfidence:F2}, above the recorded {MaxLowConfidencePrompts}. " +
            $"A description change made tool selection worse. Worst: '{low.FirstOrDefault()?.Prompt}' at {low.FirstOrDefault()?.Confidence:F2}.");
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
    // The evaluator's own accuracy. Everything else here assumes BM25 predicts tool selection; this is
    // the only test that checks it. If it falls, the scorer is what needs replacing, not the prose.
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
    public void The_model_labels_cover_exactly_the_maintained_prompts()
    {
        Assert.Equal(
            E2ETestPrompts.All.Select(p => p.Prompt).Order(),
            ModelSelections.All.Select(p => p.Prompt).Order());
    }

    /// <summary>Where the model disagrees with the prompt list, one of the two is wrong.</summary>
    [Fact]
    public void The_model_agrees_with_the_maintained_labels()
    {
        var expected = E2ETestPrompts.All.ToDictionary(p => p.Prompt, p => p.Tool, StringComparer.Ordinal);

        var conflicts = ModelSelections.All
            .Where(p => !p.Ambiguous && expected.TryGetValue(p.Prompt, out var tool) && tool != p.Tool)
            .Select(p => $"'{p.Prompt}': list says {expected[p.Prompt]}, model picked {p.Tool}")
            .ToList();

        Assert.True(conflicts.Count == 0, $"The prompt list and the model disagree:\n{string.Join("\n", conflicts)}");
    }

    /// <summary>Prints the per-tool baseline, so a release can record where selection stands.</summary>
    [Fact]
    public void Baseline()
    {
        var results = E2ETestPrompts.All
            .Select(p => (p.Tool, Selection: ToolSelectionEvaluator.Evaluate(p.Prompt, p.Tool)))
            .ToList();

        var report = new StringBuilder();
        report.AppendLine("| Tool | Prompts | Top-3 | Mean confidence | Worst prompt |");
        report.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var group in results.GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var worst = group.OrderBy(r => r.Selection.Confidence).First();
            report.AppendLine(
                $"| {group.Key} | {group.Count()} | {group.Count(r => r.Selection.InTopThree)}/{group.Count()} | " +
                $"{group.Average(r => r.Selection.Confidence):F2} | {worst.Selection.Confidence:F2} — {worst.Selection.Prompt} |");
        }

        var confident = results.Count(r => r.Selection.Confidence >= MinimumConfidence);

        report.AppendLine();
        report.AppendLine(
            $"Overall: top-3 {results.Count(r => r.Selection.InTopThree)}/{results.Count}, " +
            $"mean confidence {results.Average(r => r.Selection.Confidence):F2}, " +
            $"at or above {MinimumConfidence:F2}: {confident}/{results.Count} ({(double)confident / results.Count:P0}), " +
            $"against a target of {TargetRate:P0}.");

        foreach (var r in results.Where(x => x.Selection.Confidence < MinimumConfidence).OrderBy(x => x.Selection.Confidence))
        {
            report.AppendLine($"LOW {r.Selection.Confidence:F2} [{r.Tool}] {r.Selection.Prompt} -> {string.Join(" / ", r.Selection.Ranked.Take(3))}");
        }

        output.WriteLine(report.ToString());
    }
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
