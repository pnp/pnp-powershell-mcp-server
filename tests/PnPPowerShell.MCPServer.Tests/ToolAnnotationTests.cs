namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Clients decide what to auto-approve from the annotations, so an unstated hint is a real gap.</summary>
public class ToolAnnotationTests
{
    [Fact]
    public void Every_tool_states_its_read_only_idempotent_and_open_world_hints()
    {
        foreach (var tool in ToolCatalog.All)
        {
            var name = tool.ProtocolTool.Name;
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

    [Fact]
    public void Every_tool_describes_itself()
    {
        foreach (var tool in ToolCatalog.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.ProtocolTool.Description),
                $"{tool.ProtocolTool.Name} publishes no description, so no client can choose it.");
        }
    }
}
