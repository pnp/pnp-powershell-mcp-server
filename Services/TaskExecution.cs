using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Tools;
using System.Text.Json.Nodes;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Which tool calls may run as an MCP task, and whether a given call does.</summary>
internal static class TaskExecution
{
    private const string FirstTaskRevision = "2026-07-28";

    /// <summary>The selector handed to WithTasks.</summary>
    // Only commands are long enough to deserve a task; metadata lookups skip the polling round-trip.
    internal static McpTaskExecutionMode SelectMode(RequestContext<CallToolRequestParams> request) => SelectMode(request.Params?.Name);

    internal static McpTaskExecutionMode SelectMode(string? toolName) => toolName switch
    {
        "pnp_run_command" => McpTaskExecutionMode.Optional,
        _ => McpTaskExecutionMode.Synchronous,
    };

    /// <summary>How long a task and its result are kept, counted from creation even while it runs.</summary>
    internal static TimeSpan TimeToLive => PnPPowerShellTools.CommandTimeout + PnPPowerShellTools.ApprovalLifetime + TimeSpan.FromHours(1);

    /// <summary>True when the SDK runs this call as a task instead of answering it inline.</summary>
    internal static bool RunsAsTask(RequestContext<CallToolRequestParams> request) => RunsAsTask(request.Params?.Name, request.JsonRpcRequest);

    // Mirrors the SDK's unexposed rule in McpTasksBuilderExtensions; the stdio task tests catch drift.
    internal static bool RunsAsTask(string? toolName, JsonRpcRequest request)
    {
        if (SelectMode(toolName) is McpTaskExecutionMode.Synchronous)
        {
            return false;
        }

        var version = request.Context?.ProtocolVersion;
        if (string.IsNullOrEmpty(version) || string.CompareOrdinal(version, FirstTaskRevision) < 0)
        {
            return false;
        }

        return request.Context?.ClientCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) is true ||
               request.Params?["_meta"]?["io.modelcontextprotocol/clientCapabilities"]?["extensions"] is JsonObject extensions &&
               extensions.ContainsKey(TasksProtocol.ExtensionId);
    }
}
