using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using PnPPowerShell.MCPServer.Services;
using System.Text.Json.Nodes;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Covers the rule the confirmation gate uses to tell a task from an inline call.</summary>
public class TaskExecutionTests
{
    private const string Tasks = TasksProtocol.ExtensionId;

    [Theory]
    [InlineData("pnp_run_command", "2026-07-28", true, false, true)]
    [InlineData("pnp_run_command", "2026-07-28", false, true, true)]
    [InlineData("pnp_run_command", "2026-07-28", false, false, false)]
    [InlineData("pnp_run_command", "2025-11-25", true, true, false)]
    [InlineData("pnp_run_command", null, true, true, false)]
    [InlineData("pnp_get_best_practices", "2026-07-28", true, true, false)]
    public void A_call_runs_as_a_task_only_on_a_task_revision_with_the_extension_declared(
        string tool, string? version, bool inMeta, bool inSessionCapabilities, bool expected)
    {
        var parameters = new JsonObject { ["name"] = tool };
        if (inMeta)
        {
            parameters["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject { ["extensions"] = new JsonObject { [Tasks] = new JsonObject() } },
            };
        }

        var request = new JsonRpcRequest
        {
            Method = "tools/call",
            Params = parameters,
            Context = new JsonRpcMessageContext
            {
                ProtocolVersion = version,
                ClientCapabilities = inSessionCapabilities ? new ClientCapabilities { Extensions = new Dictionary<string, object> { [Tasks] = new JsonObject() } } : null,
            },
        };

        Assert.Equal(expected, TaskExecution.RunsAsTask(tool, request));
    }

    [Fact]
    public void Only_commands_are_offered_as_tasks()
    {
        Assert.Equal(McpTaskExecutionMode.Optional, TaskExecution.SelectMode("pnp_run_command"));
        Assert.Equal(McpTaskExecutionMode.Synchronous, TaskExecution.SelectMode("pnp_search_commands"));
    }
}
