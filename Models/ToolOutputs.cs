using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PnPPowerShell.MCPServer.Models;

// Typed tool results. Every tool returned a string until now, so clients had to parse prose; these
// records are what a client reads from CallToolResult.StructuredContent instead. The text content is
// still written alongside, because a client that ignores schemas must not lose anything.

/// <summary>What <c>pnp_search_commands</c> found.</summary>
internal sealed record CommandSearchResult
{
    /// <summary>The query as the server understood it.</summary>
    public required string Query { get; init; }

    /// <summary>Derived rather than stored, so it cannot disagree with the list it counts.</summary>
    public int Count => Commands.Count;

    /// <summary>
    /// True when the answer was cut to fit the output cap — either results dropped, or per-cmdlet
    /// detail omitted. <see cref="CommandSearchHit.Parameters"/> being absent tells the two apart.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Set when the query named a superseded alias; this is the cmdlet that replaced it.</summary>
    // On the result rather than each hit: the alias was a property of the query, not of any one match.
    public string? AliasResolvedTo { get; init; }

    /// <summary>The PnP.PowerShell version the index was built from, not the one installed here.</summary>
    public string? IndexedModuleVersion { get; init; }

    public required IReadOnlyList<CommandSearchHit> Commands { get; init; }
}

/// <summary>One cmdlet, ranked by relevance to the query.</summary>
internal sealed record CommandSearchHit
{
    public required string Name { get; init; }

    public required string Verb { get; init; }

    public required string Noun { get; init; }

    /// <summary>One line on what the cmdlet does, with PnP's permissions preamble removed.</summary>
    public required string Synopsis { get; init; }

    /// <summary>
    /// Absent, rather than empty, when the parameter list was dropped to fit the output cap. An empty
    /// array would assert the cmdlet takes no parameters, which is a different and false claim.
    /// </summary>
    public IReadOnlyList<string>? Parameters { get; init; }

    /// <summary>Up to two worked invocations, taken from the cmdlet's own help.</summary>
    public IReadOnlyList<string>? Examples { get; init; }

    /// <summary>Permissions the cmdlet's help says the caller needs.</summary>
    public IReadOnlyList<string>? RequiredPermissions { get; init; }

    public string? DocsUrl { get; init; }
}

// Source-generated and combined with the SDK's own resolver: the server publishes native AOT, where
// reflection-based serialization is unavailable.
[JsonSerializable(typeof(CommandSearchResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ToolOutputJsonContext : JsonSerializerContext;

/// <summary>The serializer options the tools are registered with, so typed results survive AOT.</summary>
internal static class ToolJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                ToolOutputJsonContext.Default,
                ModelContextProtocol.McpJsonUtilities.DefaultOptions.TypeInfoResolver),
        };

        options.MakeReadOnly();
        return options;
    }
}
