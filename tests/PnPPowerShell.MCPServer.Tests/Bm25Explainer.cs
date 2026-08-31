using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// Recomputes the scorer's arithmetic on the real corpus and prints the working, so the ranking can be
/// explained rather than trusted. Re-derives with the same formula Bm25Index uses, and asserts the order
/// it produces matches the real search — otherwise this would be a plausible story rather than the truth.
/// </summary>
public class Bm25Explainer(ITestOutputHelper output)
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    // Must mirror CommandCorpus.Index, or the explanation describes a scorer that is not the one running.
    private static readonly (Func<IndexedCommand, string?> Text, int Weight, string Name)[] Fields =
    [
        (c => c.Name, 4, "name"),
        (c => c.Noun, 3, "noun"),
        (c => c.Synopsis, 3, "synopsis"),
        (c => c.Description, 2, "description"),
        (c => c.Verb, 1, "verb"),
        (c => string.Join(' ', c.Parameters.Select(p => p.Name)), 1, "parameters"),
        (c => string.Join(' ', c.Examples ?? []), 1, "examples"),
    ];

    private static Dictionary<string, int> Frequencies(IndexedCommand command)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (text, weight, _) in Fields)
        {
            foreach (var token in Bm25Tokenizer.Tokenize(text(command)))
            {
                frequencies[token] = frequencies.GetValueOrDefault(token) + weight;
            }
        }

        return frequencies;
    }

    [Theory]
    [InlineData("add a column to a list")]
    public void Explain(string query)
    {
        var corpus = CommandCorpus.Commands;
        var docs = corpus.ToDictionary(c => c.Name, Frequencies, StringComparer.Ordinal);
        var lengths = docs.ToDictionary(d => d.Key, d => d.Value.Values.Sum(), StringComparer.Ordinal);
        var averageLength = lengths.Values.Average();

        var terms = Bm25Tokenizer.Tokenize(query).Distinct(StringComparer.Ordinal).ToList();

        output.WriteLine($"QUERY: \"{query}\"");
        output.WriteLine($"tokens after stemming and stopwords: {string.Join(", ", terms)}");
        output.WriteLine($"corpus: {corpus.Count} cmdlets, average length {averageLength:n0} weighted tokens");
        output.WriteLine("");
        output.WriteLine("TERM        docs containing    IDF   (rarer term = higher weight)");

        var idf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            var df = docs.Count(d => d.Value.ContainsKey(term));
            idf[term] = Math.Log(1 + (corpus.Count - df + 0.5) / (df + 0.5));
            output.WriteLine($"{term,-12}{df,10}       {idf[term],5:n2}");
        }

        var scored = docs
            .Select(d => (Name: d.Key, Score: terms.Sum(t => Score(d.Value, lengths[d.Key], averageLength, t, idf))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        output.WriteLine("");
        output.WriteLine("TOP 5, and where each term's weight came from");

        foreach (var (name, score) in scored.Take(5))
        {
            var command = corpus.First(c => c.Name == name);
            output.WriteLine($"  {score,5:n2}  {name}   (len {lengths[name]})");

            foreach (var term in terms.Where(t => docs[name].ContainsKey(t)))
            {
                var hits = Fields
                    .Where(f => Bm25Tokenizer.Tokenize(f.Text(command)).Contains(term))
                    .Select(f => f.Name);

                output.WriteLine(
                    $"          {term,-10} freq {docs[name][term],2}  " +
                    $"contributes {Score(docs[name], lengths[name], averageLength, term, idf):n2}  from: {string.Join(", ", hits)}");
            }
        }

        // The explanation is only worth printing if it matches what the server actually returns.
        var real = CommandCorpus.Search(query, 5).Select(c => c.Name).ToList();
        Assert.Equal(real, scored.Take(5).Select(x => x.Name).ToList());
    }

    private static double Score(Dictionary<string, int> frequencies, int length, double averageLength, string term, Dictionary<string, double> idf)
    {
        if (!frequencies.TryGetValue(term, out var frequency))
        {
            return 0;
        }

        var normalization = 1 - B + B * length / averageLength;

        return idf[term] * (frequency * (K1 + 1)) / (frequency + K1 * normalization);
    }
}
