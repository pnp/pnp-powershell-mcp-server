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

    /// <summary>The cmdlet name in its documented casing, or null when it is not a PnP cmdlet.</summary>
    public static string? Resolve(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ByName.Value.TryGetValue(name.Trim(), out var canonical) ? canonical : null;

    /// <summary>The raw markdown source of the cmdlet's documentation page, or null when unknown.</summary>
    public static string? MarkdownUrl(string? name) => Expand(Root.Value.MarkdownUrlTemplate, name);

    /// <summary>The published documentation page for the cmdlet, or null when unknown.</summary>
    public static string? DocsUrl(string? name) => Expand(Root.Value.DocsUrlTemplate, name);

    // Keyword search and the provenance line lived here to serve pnp_search_commands when pwsh was
    // unavailable. CommandCorpus answers that offline now, so both were dead weight.

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
}
