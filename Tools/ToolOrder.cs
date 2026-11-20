using ModelContextProtocol.Server;

namespace PnPPowerShell.MCPServer.Tools;

/// <summary>The order tools/list publishes: connect, run, find commands, samples, guidance, then housekeeping.</summary>
internal static class ToolOrder
{
    internal const string SelectionVariable = "PNP_MCP_TOOLS";

    internal static readonly string[] Names =
    [
        "pnp_diagnose_connection",
        "pnp_setup_environment",
        "pnp_get_connection_status",
        "pnp_run_command",
        "pnp_get_result_page",
        "pnp_search_commands",
        "pnp_get_command_docs",
        "pnp_suggest_script",
        "pnp_search_script_samples",
        "pnp_get_script_sample",
        "pnp_save_script_sample",
        "pnp_get_best_practices",
        "pnp_list_sessions",
        "pnp_reset_session",
        "pnp_ping",
    ];

    /// <summary>What "core" publishes: everything needed to connect, run and look up a cmdlet.</summary>
    // Left out: the sample tools, and the two housekeeping tools pnp_diagnose_connection overlaps.
    internal static readonly string[] Core =
    [
        "pnp_diagnose_connection",
        "pnp_setup_environment",
        "pnp_get_connection_status",
        "pnp_run_command",
        "pnp_get_result_page",
        "pnp_search_commands",
        "pnp_get_command_docs",
        "pnp_get_best_practices",
        "pnp_reset_session",
    ];

    /// <summary>Kept by every selection: the tools the server instructions name.</summary>
    internal static readonly string[] AlwaysPublished =
    [
        "pnp_diagnose_connection",
        "pnp_get_connection_status",
        "pnp_get_best_practices",
    ];

    /// <summary>Re-adds the tools in <see cref="Names"/> order; an unlisted tool keeps its relative place after them.</summary>
    internal static void Apply(McpServerPrimitiveCollection<McpServerTool> tools) =>
        Apply(tools, Environment.GetEnvironmentVariable(SelectionVariable));

    internal static void Apply(McpServerPrimitiveCollection<McpServerTool> tools, string? selection)
    {
        var keep = Select(selection, out var unknown);

        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"{SelectionVariable}: ignoring unknown entries: {string.Join(", ", unknown)}. Valid: all, core, or tool names.");
        }

        var ordered = tools
            .Where(t => keep is null || keep.Contains(t.ProtocolTool.Name))
            .OrderBy(t => Array.IndexOf(Names, t.ProtocolTool.Name) is var rank and >= 0 ? rank : int.MaxValue)
            .ToList();

        tools.Clear();
        foreach (var tool in ordered)
        {
            tools.Add(tool);
        }
    }

    /// <summary>The tools a comma-separated selection names; null for every tool.</summary>
    // Fails open: a selection naming nothing valid would otherwise publish a server with no tools.
    internal static HashSet<string>? Select(string? selection, out List<string> unknown)
    {
        unknown = [];
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in (selection ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (entry.Equals("core", StringComparison.OrdinalIgnoreCase))
            {
                keep.UnionWith(Core);
            }
            else if (Names.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                keep.Add(entry);
            }
            else
            {
                unknown.Add(entry);
            }
        }

        if (keep.Count == 0)
        {
            return null;
        }

        // The server instructions and both prompts send the model to these, whatever else is published.
        keep.UnionWith(AlwaysPublished);

        // A large result tells the model to call the paging tool, so it follows the tool that produces one.
        if (keep.Contains("pnp_run_command"))
        {
            keep.Add("pnp_get_result_page");
        }

        return keep;
    }
}
