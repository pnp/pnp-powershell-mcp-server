using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>One scored hit, most relevant first.</summary>
internal readonly record struct Bm25Hit<T>(T Document, double Score);

/// <summary>Splits text the way the corpus is indexed. Separate from the index because a generic type cannot hold a generated regex.</summary>
internal static partial class Bm25Tokenizer
{
    // Dropped rather than down-weighted. A natural-language query carries enough of these ("find sites
    // WITH NO owner") that they outvote the words that matter -- "no" alone pulls up every NoGroup cmdlet.
    // Written in natural spelling and stemmed once here, so adding a word needs no knowledge of the stemmer.
    private static readonly HashSet<string> Stopwords = new(
        new[]
        {
            "a", "all", "an", "and", "any", "are", "as", "at", "be", "by", "can", "do", "does", "for",
            "from", "has", "have", "how", "i", "if", "in", "into", "is", "it", "its", "me", "my", "need",
            "no", "not", "of", "on", "one", "only", "or", "our", "out", "over", "should", "so", "some",
            "someone", "that", "the", "their", "them", "then", "there", "these", "this", "to", "up", "us",
            "want", "was", "what", "when", "which", "who", "will", "with", "would", "you", "your",
        }.Select(Stem),
        StringComparer.Ordinal);

    /// <summary>Splits on non-alphanumerics and on camel case, so Get-PnPTenantSite yields get, pnp, tenant, site.</summary>
    public static List<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // "PnP" is capital-lower-capital, which every camel splitter breaks into "Pn" + "P". Folded to a
        // single word first, so the module's own name tokenizes the same way a user typing "pnp" does.
        text = text.Replace("PnP", "Pnp", StringComparison.OrdinalIgnoreCase);

        List<string> tokens = [];

        // One pass: every branch of the pattern matches only letters or digits, so Matches walks over
        // separators by itself. Splitting into words first and re-matching inside each was the same work twice.
        foreach (Match part in WordRegex().Matches(text))
        {
            var token = part.Value.ToLowerInvariant();
            if (token.Length <= 1)
            {
                continue;
            }

            // Stemmed before the stopword check, so the set only has to hold the stemmed forms.
            var stemmed = Stem(token);
            if (!Stopwords.Contains(stemmed))
            {
                tokens.Add(stemmed);
            }
        }

        return tokens;
    }

    /// <summary>Crude suffix fold, so "sites" matches "site" and "creating" matches "create".</summary>
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

    // Runs of capitals stay whole, so "URL" and "ID" survive as one token.
    [GeneratedRegex("[0-9]+|[A-Z]+(?![a-z])|[A-Z][a-z]*|[a-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}

/// <summary>
/// Field-weighted BM25 over a forward index built once, lazily. Modelled on the scorer in
/// ToolSelectionEvaluator, which keeps its own copy: that one is a calibrated instrument with a
/// recorded agreement threshold, so it is deliberately not sharing this implementation.
/// </summary>
internal sealed class Bm25Index<T>
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<Entry> _entries;
    private readonly Dictionary<string, int> _documentFrequencies;

    /// <param name="documents">The corpus.</param>
    /// <param name="fields">Each field's text and its weight; a term in a weighted field counts that many times.</param>
    public Bm25Index(IEnumerable<T> documents, IReadOnlyList<(Func<T, string?> Text, int Weight)> fields)
    {
        _entries = [];

        // Tokenizing allocates a fresh string per occurrence, so the corpus would otherwise hold ten
        // copies of "site" for every cmdlet that mentions it. Pooled while building, then discarded.
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        var lengths = new List<int>();

        foreach (var document in documents)
        {
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            var length = 0;

            foreach (var (text, weight) in fields)
            {
                foreach (var token in Bm25Tokenizer.Tokenize(text(document)))
                {
                    if (!pool.TryGetValue(token, out var interned))
                    {
                        interned = token;
                        pool[token] = interned;
                    }

                    frequencies[interned] = frequencies.GetValueOrDefault(interned) + weight;
                    length += weight;
                }
            }

            _entries.Add(new Entry(document, frequencies, length, 0));
            lengths.Add(length);
        }

        _documentFrequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in _entries.SelectMany(e => e.Frequencies.Keys))
        {
            _documentFrequencies[term] = _documentFrequencies.GetValueOrDefault(term) + 1;
        }

        // Length normalization depends only on the document, so it is settled here rather than
        // recomputed for every (document, term) pair at query time.
        var averageLength = lengths.Count > 0 ? lengths.Average() : 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            var normalization = averageLength > 0 ? 1 - B + B * _entries[i].Length / averageLength : 1;
            _entries[i] = _entries[i] with { Normalization = normalization };
        }
    }

    /// <summary>The highest-scoring documents for <paramref name="query"/>, best first.</summary>
    public IReadOnlyList<Bm25Hit<T>> Search(string? query, int limit, Func<T, string> tieBreaker)
    {
        var terms = Bm25Tokenizer.Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0 || limit <= 0)
        {
            return [];
        }

        // IDF depends only on the term, so it is computed once per query rather than once per document.
        // It neutralises a term every document shares -- "pnp" costs nothing without a stopword list.
        var weights = new (string Term, double Idf)[terms.Count];
        for (var i = 0; i < terms.Count; i++)
        {
            var documentFrequency = _documentFrequencies.GetValueOrDefault(terms[i]);
            weights[i] = (terms[i], Math.Log(1 + (_entries.Count - documentFrequency + 0.5) / (documentFrequency + 0.5)));
        }

        return [.. _entries
            .Select(e => new Bm25Hit<T>(e.Document, Score(e, weights)))
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => tieBreaker(h.Document), StringComparer.OrdinalIgnoreCase)
            .Take(limit)];
    }

    private static double Score(Entry entry, (string Term, double Idf)[] weights)
    {
        var score = 0.0;

        foreach (var (term, idf) in weights)
        {
            if (entry.Frequencies.TryGetValue(term, out var frequency))
            {
                score += idf * (frequency * (K1 + 1)) / (frequency + K1 * entry.Normalization);
            }
        }

        return score;
    }

    private sealed record Entry(T Document, Dictionary<string, int> Frequencies, int Length, double Normalization);
}
