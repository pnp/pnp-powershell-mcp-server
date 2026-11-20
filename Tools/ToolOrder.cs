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

    /// <summary>Re-adds the tools in <see cref="Names"/> order; an unlisted tool keeps its relative place after them.</summary>
    internal static void Apply(McpServerPrimitiveCollection<McpServerTool> tools) =>
        Apply(tools, Environment.GetEnvironmentVariable(SelectionVariable));

    internal static void Apply(McpServerPrimitiveCollection<McpServerTool> tools, string? selection)
    {
        var keep = Select(selection, out var unknown);

        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"{SelectionVariable}: ignoring unknown entries: {string.Join(", ", unknown)}. Valid: all, core, or the names of tools to publish besides the core ones.");
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

    /// <summary>The core tools plus any a comma-separated selection names; null for every tool.</summary>
    // Core is the floor: the instructions, both prompts, the diagnosis and large results all send the model to core tools.
    // Fails open: a selection naming nothing valid publishes every tool.
    internal static HashSet<string>? Select(string? selection, out List<string> unknown)
    {
        unknown = [];
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = false;

        foreach (var entry in (selection ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (entry.Equals("core", StringComparison.OrdinalIgnoreCase))
            {
                valid = true;
            }
            else if (Names.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                keep.Add(entry);
                valid = true;
            }
            else
            {
                unknown.Add(entry);
            }
        }

        if (!valid)
        {
            return null;
        }

        keep.UnionWith(Core);

        // Both of these answer with sample names and say to fetch one.
        if (keep.Contains("pnp_search_script_samples") || keep.Contains("pnp_suggest_script"))
        {
            keep.Add("pnp_get_script_sample");
        }

        return keep;
    }

    /// <summary>Tells a reader of the guidance which tools it names are not published here; empty when all are.</summary>
    internal static string UnpublishedNote() => UnpublishedNote(Environment.GetEnvironmentVariable(SelectionVariable));

    internal static string UnpublishedNote(string? selection) =>
        Select(selection, out _) is { } keep
            ? $"\n\nNOTE: This server is set up to publish only some of its tools ({SelectionVariable}). Not available here: " +
              $"{string.Join(", ", Names.Where(n => !keep.Contains(n)))}. Skip any step above that names one of them."
            : string.Empty;
}
