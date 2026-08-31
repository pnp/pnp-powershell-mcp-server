using PnPPowerShell.MCPServer.Models;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>
/// The searchable cmdlet corpus: synopsis, parameters, parameter sets and examples for every cmdlet in
/// the module this server was built against. Scored with BM25 in process, so search costs no pwsh
/// round-trip. <see cref="CommandIndex"/> remains the authority on names and documentation URLs.
/// </summary>
internal static class CommandCorpus
{
    private static readonly Lazy<CommandIndexRoot> Root = new(Load);

    private static readonly Lazy<Dictionary<string, IndexedCommand>> ByName = new(() =>
        Root.Value.Commands
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));

    // Rebuilt rather than used as deserialized: System.Text.Json gives a case-sensitive dictionary, and
    // PowerShell command names are not case-sensitive, so "add-pnpazureadgroupmember" would not resolve.
    private static readonly Lazy<Dictionary<string, string>> Aliases = new(() =>
        (Root.Value.Aliases ?? []).ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    // Name and noun carry the signal; the verb is one of about twenty values across the whole corpus.
    // Parameters and examples earn a low weight: they match real queries ("filter", "batch") but a
    // cmdlet with forty parameters must not outrank one whose synopsis actually answers the question.
    // Every field counts toward document length. Exempting the parameter list was tried and reverted:
    // it let Set-PnPTenant, with some two hundred parameters, match almost any query at no cost.
    private static readonly Lazy<Bm25Index<IndexedCommand>> Index = new(() =>
        new Bm25Index<IndexedCommand>(
            Root.Value.Commands,
            [
                (c => c.Name, 4),
                (c => c.Noun, 3),
                (c => c.Synopsis, 3),
                (c => c.Description, 2),
                (c => c.Verb, 1),
                (c => string.Join(' ', c.Parameters.Select(p => p.Name)), 1),
                (c => string.Join(' ', c.Examples ?? []), 1),
            ]));

    public static IReadOnlyList<IndexedCommand> Commands => Root.Value.Commands;

    /// <summary>The module version the corpus was generated from, so a stale index is visible rather than silent.</summary>
    public static string? ModuleVersion => Root.Value.ModuleVersion;

    public static string Provenance =>
        $"Indexed from PnP.PowerShell {ModuleVersion ?? "unknown"}. " +
        "Your installed module may differ; 'pnp_get_command_docs' reads the module you actually have.";

    /// <summary>The indexed cmdlet, following a superseded alias to its current name. Null when unknown.</summary>
    public static IndexedCommand? Lookup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();

        if (ByName.Value.TryGetValue(trimmed, out var direct))
        {
            return direct;
        }

        return Aliases.Value.TryGetValue(trimmed, out var target) ? ByName.Value.GetValueOrDefault(target) : null;
    }

    /// <summary>The current name for a superseded alias, or null when the name is not an alias.</summary>
    public static string? AliasTarget(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Aliases.Value.TryGetValue(name.Trim(), out var target) ? target : null;

    /// <summary>Relevance-ranked cmdlets for a free-text query. No network, no pwsh.</summary>
    public static IReadOnlyList<IndexedCommand> Search(string? query, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var ranked = Index.Value.Search(query, limit, c => c.Name).Select(h => h.Document);

        // Someone who typed a whole cmdlet name wants that cmdlet, and BM25 alone does not guarantee it:
        // a near-namesake with a shorter synopsis outscores it. Hoisted rather than weighted, because
        // "rank the thing I named first" is a rule, not a preference.
        return Lookup(query) is { } exact
            ? [exact, .. ranked.Where(c => !NameEquals(c, exact)).Take(limit - 1)]
            : [.. ranked];
    }

    // By name rather than reference: the index happens to hand back the same instances today, but that
    // would stop being true the moment anything projects or filters documents into it.
    private static bool NameEquals(IndexedCommand a, IndexedCommand b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The documentation page for a cmdlet, preferring an off-template HelpUri when it has one.</summary>
    public static string? DocsUrl(IndexedCommand command) =>
        command.HelpUri is { Length: > 0 } uri
            ? uri
            : Root.Value.DocsUrlTemplate is { Length: > 0 } template
                ? template.Replace("{name}", command.Name)
                : null;

    private static CommandIndexRoot Load()
    {
        using var stream = typeof(CommandCorpus).Assembly.GetManifestResourceStream("pnp-index.json")
            ?? throw new InvalidOperationException("pnp-index.json is missing from the assembly; it must be an EmbeddedResource.");

        // Parsed from the stream rather than a string: this resource is ~650 KB, and reading it to a
        // string first would allocate 1.3 MB on the large object heap only to transcode it straight back.
        return JsonSerializer.Deserialize(stream, CommandIndexJsonContext.Default.CommandIndexRoot)
            ?? throw new InvalidOperationException("The vendored pnp-index.json could not be parsed.");
    }
}
