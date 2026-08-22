using PnPPowerShell.MCPServer.Models;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>The catalogue, resolved once. Overrides first, then the compiled-in copy.</summary>
internal static class ScriptSampleIndex
{
    private static readonly Lazy<(List<ScriptSample> Samples, string Provenance)> Loaded = new(Load);

    public static IReadOnlyList<ScriptSample> Samples => Loaded.Value.Samples;

    /// <summary>One line naming where the index came from, so a stale index is visible rather than silent.</summary>
    public static string Provenance => Loaded.Value.Provenance;

    private static (List<ScriptSample>, string) Load()
    {
        // Overrides must not throw: Lazy caches the exception and would disable the tools.
        if (Safely(ReadExtension) is { Count: > 0 } fromExtension)
        {
            return (fromExtension, $"Index: {fromExtension.Count} samples, from the PnP PowerShell VS Code extension.");
        }

        if (Safely(ReadLocalClone) is { Count: > 0 } fromClone)
        {
            return (fromClone, $"Index: {fromClone.Count} samples, from PNP_SCRIPT_SAMPLES_PATH.");
        }

        using var stream = typeof(ScriptSampleIndex).Assembly.GetManifestResourceStream("script-samples.json")
            ?? throw new InvalidOperationException("script-samples.json is missing from the assembly; it must be an EmbeddedResource.");

        using var reader = new StreamReader(stream);
        var root = JsonSerializer.Deserialize(reader.ReadToEnd(), ScriptSampleJsonContext.Default.SamplesRoot)
            ?? throw new InvalidOperationException("The vendored script-samples.json could not be parsed.");

        Normalize(root);

        return (root.Samples,
            $"Index: {root.Samples.Count} samples, vendored at commit {Short(root.Commit)} ({root.Generated}). " +
            "A newer sample will not be listed until this server is updated; browse https://pnp.github.io/script-samples/ for the live catalogue.");
    }

    private static List<ScriptSample>? Safely(Func<List<ScriptSample>?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static List<ScriptSample>? ReadExtension()
    {
        if (FindExtensionSamplesJson() is not { } path)
        {
            return null;
        }

        var root = JsonSerializer.Deserialize(File.ReadAllText(path), ScriptSampleJsonContext.Default.SamplesRoot);
        if (root is null)
        {
            return null;
        }

        Normalize(root);
        return root.Samples;
    }

    /// <summary>True when a name is one plain folder segment. It reaches a path and a URL.</summary>
    private static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 128 &&
        name == Path.GetFileName(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        name.Trim('.').Length > 0;

    /// <summary>Fills in name, url and rawUrl, and drops nulls the sources leave.</summary>
    internal static void Normalize(SamplesRoot root)
    {
        root.Samples.RemoveAll(s => s is null);

        foreach (var sample in root.Samples)
        {
            sample.Tags = [.. (sample.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t))];
            sample.Authors = [.. (sample.Authors ?? []).Where(a => !string.IsNullOrWhiteSpace(a?.Name))];
            sample.Name ??= string.Empty;
            sample.Title ??= string.Empty;
            sample.Description ??= string.Empty;
            sample.Url ??= string.Empty;
            sample.RawUrl ??= string.Empty;

            if (sample.Name.Length == 0 && sample.RawUrl.Length > 0)
            {
                var segments = sample.RawUrl.TrimEnd('/').Split('/');
                var readme = Array.IndexOf(segments, "README.md");
                if (readme > 0)
                {
                    sample.Name = segments[readme - 1];
                }
            }

            if (sample.Url.Length == 0 && root.UrlTemplate is { Length: > 0 } urlTemplate)
            {
                sample.Url = urlTemplate.Replace("{name}", sample.Name);
            }

            if (sample.RawUrl.Length == 0 && root.RawUrlTemplate is { Length: > 0 } rawTemplate)
            {
                sample.RawUrl = rawTemplate.Replace("{name}", sample.Name);
            }
        }

        // Dropped rather than repaired: the intent is unknowable.
        root.Samples.RemoveAll(s => !IsSafeName(s.Name));
    }

    /// <summary>The samples.json shipped inside the PnP PowerShell VS Code extension, if it is installed.</summary>
    private static string? FindExtensionSamplesJson()
    {
        var extensions = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");

        if (!Directory.Exists(extensions))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(extensions, "adamwojcikit.pnp-powershell-extension-*"))
        {
            var candidate = Path.Combine(dir, "out", "data", "samples.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>A pnp/script-samples checkout named by PNP_SCRIPT_SAMPLES_PATH, read from its per-sample assets/sample.json.</summary>
    private static List<ScriptSample>? ReadLocalClone()
    {
        var root = Environment.GetEnvironmentVariable("PNP_SCRIPT_SAMPLES_PATH");
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var scripts = Path.Combine(root, "scripts");
        if (!Directory.Exists(scripts))
        {
            return null;
        }

        var samples = new List<ScriptSample>();

        foreach (var dir in Directory.EnumerateDirectories(scripts))
        {
            var manifest = Path.Combine(dir, "assets", "sample.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var name = Path.GetFileName(dir);
                    samples.Add(new ScriptSample
                    {
                        Name = name,
                        Title = Text(element, "title"),
                        Description = Text(element, "shortDescription"),
                        Url = Text(element, "url"),
                        RawUrl = $"https://raw.githubusercontent.com/pnp/script-samples/main/scripts/{name}/README.md",
                        Tags = element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                            ? [.. tags.EnumerateArray().Select(t => t.GetString() ?? string.Empty)]
                            : [],
                    });
                }
            }
            catch (JsonException)
            {
                // A malformed sample manifest skips that sample rather than the whole checkout.
            }
        }

        // Restated because this path does not run through Normalize.
        samples.RemoveAll(s => !IsSafeName(s.Name));

        return samples;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string Short(string? commit) =>
        string.IsNullOrWhiteSpace(commit) ? "unknown" : commit[..Math.Min(7, commit.Length)];
}
