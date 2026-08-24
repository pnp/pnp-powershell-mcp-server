using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>How one prompt ranked against every tool.</summary>
internal sealed record Selection(string Prompt, string Expected, IReadOnlyList<string> Ranked)
{
    public int Rank => Ranked.ToList().IndexOf(Expected) + 1;

    public bool InTopThree => Rank is > 0 and <= 3;
}

/// <summary>Scores which tool a prompt selects, from the published descriptions alone.</summary>
internal static class ToolSelectionEvaluator
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    // Removed rather than down-weighted: on a corpus this small a common term still swings a close ranking.
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "all", "an", "and", "any", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from",
        "get", "has", "have", "how", "i", "in", "is", "it", "its", "me", "my", "of", "on", "one", "or",
        "our", "out", "so", "that", "the", "their", "them", "then", "there", "these", "this", "to", "use",
        "used", "using", "want", "was", "what", "when", "which", "will", "with", "you", "your",
        "pnp", "powershell", "sharepoint", "microsoft", "365", "tool", "tools", "command", "commands",
    };

    private static readonly Lazy<Corpus> Index = new(Build);

    /// <summary>Ranks every tool for <paramref name="prompt"/> and reports where the expected one landed.</summary>
    public static Selection Evaluate(string prompt, string expected)
    {
        var corpus = Index.Value;
        var terms = Tokenize(prompt);

        var scored = corpus.Documents
            .Select(d => (d.Name, Score: Score(d, terms, corpus)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        // Ranking only. A confidence score lived here and never caught a regression.
        return new Selection(prompt, expected, [.. scored.Select(x => x.Name)]);
    }

    private static double Score(Document document, IReadOnlyList<string> terms, Corpus corpus)
    {
        var score = 0.0;

        foreach (var term in terms.Distinct(StringComparer.Ordinal))
        {
            if (!document.Frequencies.TryGetValue(term, out var frequency))
            {
                continue;
            }

            var documentFrequency = corpus.DocumentFrequencies[term];
            var idf = Math.Log(1 + (corpus.Documents.Count - documentFrequency + 0.5) / (documentFrequency + 0.5));
            var normalization = 1 - B + B * document.Length / corpus.AverageLength;

            score += idf * (frequency * (K1 + 1)) / (frequency + K1 * normalization);
        }

        return score;
    }

    private static Corpus Build()
    {
        var documents = ToolCatalog.All
            .Select(t =>
            {
                var tokens = Tokenize(ToolCatalog.SelectionText(t));
                return new Document(
                    t.ProtocolTool.Name,
                    tokens.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                    tokens.Count);
            })
            .ToList();

        var documentFrequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in documents.SelectMany(d => d.Frequencies.Keys))
        {
            documentFrequencies[term] = documentFrequencies.GetValueOrDefault(term) + 1;
        }

        return new Corpus(documents, documentFrequencies, documents.Average(d => d.Length));
    }

    private static List<string> Tokenize(string text) =>
        [.. Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !Stopwords.Contains(t))
            .Select(Stem)];

    /// <summary>Crude suffix fold, so "creating" matches "create". Non-words are fine.</summary>
    private static string Stem(string token)
    {
        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
        {
            token = token[..^3] + "y";
        }
        else if (token.Length > 4 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
        {
            token = token[..^1];
        }

        if (token.Length > 5 && token.EndsWith("ion", StringComparison.Ordinal))
        {
            token = token[..^3];
        }
        else if (token.Length > 5 && token.EndsWith("ing", StringComparison.Ordinal))
        {
            token = token[..^3];
        }
        else if (token.Length > 4 && token.EndsWith("ed", StringComparison.Ordinal))
        {
            token = token[..^2];
        }

        return token.Length > 4 && token.EndsWith('e') ? token[..^1] : token;
    }

    private sealed record Document(string Name, Dictionary<string, int> Frequencies, int Length);

    private sealed record Corpus(List<Document> Documents, Dictionary<string, int> DocumentFrequencies, double AverageLength);
}
