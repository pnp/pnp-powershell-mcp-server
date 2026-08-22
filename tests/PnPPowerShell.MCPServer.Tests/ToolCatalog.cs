using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Reflection;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Every tool this server publishes, built the way the host builds them.</summary>
internal static class ToolCatalog
{
    private static readonly Lazy<IReadOnlyList<McpServerTool>> Tools = new(Build);

    public static IReadOnlyList<McpServerTool> All => Tools.Value;

    /// <summary>Everything a client sees when deciding whether to call a tool.</summary>
    // Weighted: a parameter description qualifies one argument.
    private const int FieldWeight = 3;

    public static string SelectionText(McpServerTool tool)
    {
        var parts = new List<string>();

        for (var i = 0; i < FieldWeight; i++)
        {
            parts.Add(tool.ProtocolTool.Name.Replace('_', ' '));
            parts.Add(tool.ProtocolTool.Description ?? string.Empty);
        }

        if (tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                parts.Add(property.Name);
                if (property.Value.TryGetProperty("description", out var description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    parts.Add(description.GetString() ?? string.Empty);
                }
            }
        }

        return string.Join(' ', parts);
    }

    private static IReadOnlyList<McpServerTool> Build()
    {
        var services = new ServiceCollection().AddSingleton<PowerShellSessionManager>().BuildServiceProvider();
        var options = new McpServerToolCreateOptions { Services = services };

        return
        [
            .. Methods(typeof(PnPPowerShellTools)).Select(m => McpServerTool.Create(m, target: null, options)),
            .. Methods(typeof(ScriptSampleTools)).Select(m => McpServerTool.Create(m, target: null, options)),
        ];
    }

    private static IEnumerable<MethodInfo> Methods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
