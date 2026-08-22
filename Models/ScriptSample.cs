using System.Text.Json.Serialization;

namespace PnPPowerShell.MCPServer.Models;


[JsonSerializable(typeof(SamplesRoot))]
[JsonSerializable(typeof(CommandsRoot))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ScriptSampleJsonContext : JsonSerializerContext { }

// One shape for both sources: the extension carries url/rawUrl per sample, the vendored file templates them.

internal sealed class SamplesRoot
{
    public string? Commit { get; set; }
    public string? Generated { get; set; }
    public string? UrlTemplate { get; set; }
    public string? RawUrlTemplate { get; set; }
    public List<ScriptSample> Samples { get; set; } = [];
}

internal sealed class ScriptSample
{
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

internal sealed class CommandsRoot
{
    public string? Commit { get; set; }
    public string? Generated { get; set; }
    public string MarkdownUrlTemplate { get; set; } = string.Empty;
    public string DocsUrlTemplate { get; set; } = string.Empty;
    public List<string> Commands { get; set; } = [];
}
