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
    public string? SourceDate { get; set; }
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

// Commit and SourceDate were dropped with CommandIndex.Provenance, their only reader: the corpus
// states its own provenance now. The generator still writes them; they are simply not deserialized.
internal sealed class CommandsRoot
{
    private List<string> _commands = [];

    public string MarkdownUrlTemplate { get; set; } = string.Empty;

    public string DocsUrlTemplate { get; set; } = string.Empty;

    /// <summary>Guarded like the corpus models: an explicit JSON null would otherwise replace the initializer.</summary>
    public List<string> Commands
    {
        get => _commands;
        set => _commands = value ?? [];
    }
}
