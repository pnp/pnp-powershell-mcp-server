using ModelContextProtocol.Server;

namespace PnPPowerShell.MCPServer.Tools;

/// <summary>The order tools/list publishes: connect, run, find commands, samples, guidance, then housekeeping.</summary>
internal static class ToolOrder
{
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

    /// <summary>Re-adds the tools in <see cref="Names"/> order; an unlisted tool keeps its relative place after them.</summary>
    internal static void Apply(McpServerPrimitiveCollection<McpServerTool> tools)
    {
        var ordered = tools.OrderBy(t => Array.IndexOf(Names, t.ProtocolTool.Name) is var rank and >= 0 ? rank : int.MaxValue).ToList();

        tools.Clear();
        foreach (var tool in ordered)
        {
            tools.Add(tool);
        }
    }
}
