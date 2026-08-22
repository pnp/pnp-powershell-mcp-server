using PnPPowerShell.MCPServer.Models;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Vendored cmdlet names and where each is documented. Answers without pwsh.</summary>
internal static class CommandIndex
{
    private static readonly Lazy<CommandsRoot> Root = new(Load);

    private static readonly Lazy<Dictionary<string, string>> ByName = new(() =>
        Root.Value.Commands.ToDictionary(c => c, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Commands => Root.Value.Commands;

    public static string Provenance =>
        $"Vendored cmdlet index: {Root.Value.Commands.Count} cmdlets at commit {Short(Root.Value.Commit)} ({Root.Value.Generated}). " +
        "It lists the cmdlets that existed when this server was built, not the ones your installed module has.";

    /// <summary>The cmdlet name in its documented casing, or null when it is not a PnP cmdlet.</summary>
    public static string? Resolve(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ByName.Value.TryGetValue(name.Trim(), out var canonical) ? canonical : null;

    /// <summary>The raw markdown source of the cmdlet's documentation page, or null when unknown.</summary>
    public static string? MarkdownUrl(string? name) => Expand(Root.Value.MarkdownUrlTemplate, name);

    /// <summary>The published documentation page for the cmdlet, or null when unknown.</summary>
    public static string? DocsUrl(string? name) => Expand(Root.Value.DocsUrlTemplate, name);

    /// <summary>Keyword search over the vendored names, scored the same way the live search scores.</summary>
    public static IReadOnlyList<string> Search(IReadOnlyList<string> terms, int limit) =>
        [.. Root.Value.Commands
            .Select(name => (Name: name, Score: Score(name, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Name)];

    private static int Score(string name, IReadOnlyList<string> terms)
    {
        var dash = name.IndexOf('-');
        var verb = dash > 0 ? name[..dash] : name;
        var noun = dash > 0 ? name[(dash + 1)..] : string.Empty;
        var score = 0;

        foreach (var term in terms)
        {
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (verb.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 4;
            if (noun.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 6;
        }

        return score;
    }

    private static string? Expand(string template, string? name) =>
        Resolve(name) is { } canonical && template.Length > 0 ? template.Replace("{name}", canonical) : null;

    private static CommandsRoot Load()
    {
        using var stream = typeof(CommandIndex).Assembly.GetManifestResourceStream("pnp-commands.json")
            ?? throw new InvalidOperationException("pnp-commands.json is missing from the assembly; it must be an EmbeddedResource.");

        using var reader = new StreamReader(stream);

        return JsonSerializer.Deserialize(reader.ReadToEnd(), ScriptSampleJsonContext.Default.CommandsRoot)
            ?? throw new InvalidOperationException("The vendored pnp-commands.json could not be parsed.");
    }

    private static string Short(string? commit) =>
        string.IsNullOrWhiteSpace(commit) ? "unknown" : commit[..Math.Min(7, commit.Length)];
}
