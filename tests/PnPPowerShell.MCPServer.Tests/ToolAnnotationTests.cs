using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Reflection;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Clients decide what to auto-approve from the annotations, so an unstated hint is a real gap.</summary>
public class ToolAnnotationTests
{
    [Fact]
    public void Every_tool_states_its_read_only_idempotent_and_open_world_hints()
    {
        foreach (var (name, tool) in AllTools())
        {
            var annotations = tool.ProtocolTool.Annotations;

            Assert.True(annotations is not null, $"{name} publishes no annotations at all.");
            Assert.True(annotations!.ReadOnlyHint.HasValue, $"{name} does not state readOnlyHint.");
            Assert.True(annotations.IdempotentHint.HasValue, $"{name} does not state idempotentHint.");
            Assert.True(annotations.OpenWorldHint.HasValue, $"{name} does not state openWorldHint.");

            if (annotations.ReadOnlyHint is false)
            {
                Assert.True(annotations.DestructiveHint.HasValue, $"{name} can change state but does not state destructiveHint.");
            }
        }
    }

    private static IEnumerable<(string Name, McpServerTool Tool)> AllTools()
    {
        var services = new ServiceCollection()
            .AddSingleton<PowerShellSessionManager>()
            .BuildServiceProvider();

        var options = new McpServerToolCreateOptions { Services = services };

        foreach (var method in Methods(typeof(PnPPowerShellTools)))
        {
            yield return (method.Name, McpServerTool.Create(method, target: null, options));
        }

        var sampleTools = new ScriptSampleTools();

        foreach (var method in Methods(typeof(ScriptSampleTools)))
        {
            yield return (method.Name, McpServerTool.Create(method, sampleTools, options));
        }
    }

    private static IEnumerable<MethodInfo> Methods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
