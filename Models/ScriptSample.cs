using System.Text.Json.Serialization;

namespace PnPPowerShell.MCPServer.Models;


[JsonSerializable(typeof(ExtensionSamplesRoot))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ScriptSampleJsonContext : JsonSerializerContext { }

// Data model matching the PnP PowerShell VS Code extension's samples.json

internal sealed class ExtensionSamplesRoot
{
    public List<ScriptSample> Samples { get; set; } = [];
}

internal sealed class ScriptSample
{
    // Folder-name slug derived from rawUrl at load time (e.g. "spo-create-documentset")
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string RawUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TabTag { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<ScriptSampleAuthor> Authors { get; set; } = [];
}

internal sealed class ScriptSampleAuthor
{
    public string Name { get; set; } = string.Empty;
}
