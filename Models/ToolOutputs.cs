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
    /// How many cmdlets matched, before anything was dropped to fit the output cap. Carried so the
    /// answer can say "showing 6 of 40" instead of stating the page as though it were the whole result.
    /// </summary>
    public required int Matched { get; init; }

    /// <summary>Derived, so it cannot claim the answer is whole while listing fewer than it found.</summary>
    public bool Truncated => Count < Matched;

    /// <summary>
    /// True when per-cmdlet detail was dropped to fit — a separate fact from <see cref="Truncated"/>,
    /// which is about how many cmdlets are listed rather than how much is said about each.
    /// </summary>
    public bool DetailOmitted { get; init; }

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

/// <summary>What <c>pnp_ping</c> reports.</summary>
// This tool hand-built its JSON into a string, which is the exact failure structured output exists to
// fix: the shape was already a contract, just an unschema'd one a client had to parse out of prose.
internal sealed record ServerHealth
{
    public required string Status { get; init; }

    public required string Version { get; init; }

    public required string PackageVersion { get; init; }

    public required string Uptime { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>True when PNP_MCP_READONLY blocks state-changing cmdlets.</summary>
    public required bool ReadOnlyMode { get; init; }

    public required int ActiveSessions { get; init; }

    // Readiness is nullable throughout because the probe is optional. Absent means "not checked", which
    // is a different claim from false: a client that read false would tell the user to install something
    // they may already have.

    /// <summary>Whether PowerShell 7 is on PATH, or null when readiness was not probed.</summary>
    public bool? PwshAvailable { get; init; }

    /// <summary>Whether the PnP.PowerShell module is installed, or null when readiness was not probed.</summary>
    public bool? PnpModuleInstalled { get; init; }

    /// <summary>The installed module version, or null when it is absent or was not probed.</summary>
    public string? PnpModuleVersion { get; init; }
}

/// <summary>What <c>pnp_list_sessions</c> found.</summary>
internal sealed record SessionListResult
{
    /// <summary>How many are listed here, which is fewer than <see cref="Total"/> when truncated.</summary>
    public int Count => Sessions.Count;

    /// <summary>
    /// How many sessions actually exist. Carried separately because "N active sessions" is a claim about
    /// the machine, not about how many fitted the output cap — reporting the page as the total is a
    /// false statement rather than a truncated one.
    /// </summary>
    public required int Total { get; init; }

    public bool Truncated => Count < Total;

    public required IReadOnlyList<SessionSummary> Sessions { get; init; }
}

internal sealed record SessionSummary
{
    public required string Id { get; init; }

    /// <summary>One of <c>running</c>, <c>idle</c> or <c>stopped</c>.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset LastUsedUtc { get; init; }
}

/// <summary>
/// Where one page of a held result set sits, so a client can drive paging without parsing the MORE line.
///
/// The rows themselves stay in the text half only. They are the bulk of the payload, and repeating them
/// here would halve how many fit the output cap to restate what the caller already has.
/// </summary>
internal sealed record ResultPage
{
    public required string Cursor { get; init; }

    public required string SessionId { get; init; }

    /// <summary>Zero-based row this page starts at, after clamping.</summary>
    public required int Offset { get; init; }

    /// <summary>Rows in the whole result set, including any too large to hold.</summary>
    public required int TotalRows { get; init; }

    /// <summary>Rows held for paging; fewer than <see cref="TotalRows"/> when the set was too large.</summary>
    public required int PageableRows { get; init; }

    /// <summary>Offset to pass for the next page, or null at the end of the held rows.</summary>
    public int? NextOffset { get; init; }
}

/// <summary>What one session is connected to, as reported by <c>pnp_get_connection_status</c>.</summary>
// Deserialized from the JSON the session itself emits, then re-emitted with the session id attached, so
// a client reads one typed object instead of finding JSON inside prose.
internal sealed record ConnectionStatus
{
    public string SessionId { get; init; } = string.Empty;

    public bool Connected { get; init; }

    public string? Url { get; init; }

    public string? TenantAdminUrl { get; init; }

    /// <summary>How the connection was made, e.g. <c>O365</c> or <c>TenantAdmin</c>.</summary>
    public string? ConnectionType { get; init; }

    public string? Account { get; init; }

    /// <summary>Why there is no connection, when there is none.</summary>
    public string? Message { get; init; }
}

// Source-generated and combined with the SDK's own resolver: the server publishes native AOT, where
// reflection-based serialization is unavailable.
[JsonSerializable(typeof(CommandSearchResult))]
[JsonSerializable(typeof(ConnectionStatus))]
[JsonSerializable(typeof(ServerHealth))]
[JsonSerializable(typeof(SessionListResult))]
[JsonSerializable(typeof(ResultPage))]
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
