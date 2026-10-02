using PnPPowerShell.MCPServer.Models;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>The catalogue, resolved once: community samples plus the user's own from PNP_SCRIPT_SAMPLES_PATH.</summary>
internal static partial class ScriptSampleIndex
{
    private const string PathVariable = "PNP_SCRIPT_SAMPLES_PATH";

    /// <summary>A community sample outranks the user's own script only by matching more than twice as well.</summary>
    private const double LocalBoost = 2;

    private const int MaxLocalScripts = 5_000;

    internal const long MaxScriptBytes = 1_000_000;

    private sealed record Catalogue(List<ScriptSample> Samples, string Provenance, Bm25Index<ScriptSample> Index);

    private static volatile Lazy<Catalogue> _catalogue = new(Load);

    public static IReadOnlyList<ScriptSample> Samples => _catalogue.Value.Samples;

    /// <summary>One line naming where the index came from, so a stale index is visible rather than silent.</summary>
    public static string Provenance => _catalogue.Value.Provenance;

    /// <summary>Rereads every source on next use.</summary>
    internal static void Reload() => _catalogue = new(Load);

    /// <summary>Relevance-ranked samples for a free-text query, best first.</summary>
    public static IReadOnlyList<Bm25Hit<ScriptSample>> Search(string? query, int limit) =>
        limit <= 0
            ? []
            : [.. _catalogue.Value.Index.Search(query, int.MaxValue, s => s.Name)
                .Select(h => h.Document.LocalPath.Length > 0 ? h with { Score = h.Score * LocalBoost } : h)
                .OrderByDescending(h => h.Score)
                .Take(limit)];

    /// <summary>The Git URLs and full folder paths PNP_SCRIPT_SAMPLES_PATH lists, separated by ';'.</summary>
    // A relative path would resolve against whatever directory the client launched the server in.
    private static string[] Entries() =>
        [.. (Environment.GetEnvironmentVariable(PathVariable) ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => SampleRepository.IsUrl(e) || Path.IsPathFullyQualified(e))];

    /// <summary>Where new samples go: the first listed folder that is neither a Git copy nor a pnp/script-samples clone.</summary>
    internal static string? SaveFolder() =>
        Entries().FirstOrDefault(e => !SampleRepository.IsUrl(e) && IsPlainFolder(e));

    /// <summary>False for a clone, and for a folder that cannot be read now, since it may yet turn out to be one.</summary>
    private static bool IsPlainFolder(string folder)
    {
        try
        {
            return !IsClone(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Judged by layout rather than by whether its manifests parse, so a broken clone is still one.</summary>
    private static bool IsClone(string folder)
    {
        var scripts = Path.Combine(folder, "scripts");
        return Directory.Exists(scripts) &&
            Directory.EnumerateDirectories(scripts).Any(d => File.Exists(Path.Combine(d, "assets", "sample.json")));
    }

    /// <summary>Every sample in those entries, syncing Git copies first. An unreadable entry skips only itself.</summary>
    internal static List<ScriptSample> ReadLocal() =>
        [.. Entries().SelectMany(e => Safely(() => ReadFolder(SampleRepository.IsUrl(e) ? SampleRepository.Sync(e) : e)) ?? [])];

    private static Catalogue Load()
    {
        var (samples, provenance) = LoadCommunity();
        List<ScriptSample> local = [.. ReadLocal().DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

        if (local.Count > 0)
        {
            samples = [.. local.Concat(samples).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
            provenance += $" Plus {local.Count} from {PathVariable}.";
        }

        return new Catalogue(samples, provenance, new Bm25Index<ScriptSample>(
            samples,
            [
                (s => s.Title, 4),
                (s => s.Name, 3),
                (s => string.Join(' ', s.Tags), 3),
                (s => s.Description, 2),
            ]));
    }

    private static (List<ScriptSample>, string) LoadCommunity()
    {
        // Overrides must not throw: Lazy caches the exception and would disable the tools.
        if (Safely(ReadExtension) is { Count: > 0 } fromExtension)
        {
            return (fromExtension, $"Index: {fromExtension.Count} samples, from the PnP PowerShell VS Code extension.");
        }

        using var stream = typeof(ScriptSampleIndex).Assembly.GetManifestResourceStream("script-samples.json")
            ?? throw new InvalidOperationException("script-samples.json is missing from the assembly; it must be an EmbeddedResource.");

        using var reader = new StreamReader(stream);
        var root = JsonSerializer.Deserialize(reader.ReadToEnd(), ScriptSampleJsonContext.Default.SamplesRoot)
            ?? throw new InvalidOperationException("The vendored script-samples.json could not be parsed.");

        Normalize(root);

        return (root.Samples,
            $"Index: {root.Samples.Count} samples, vendored at commit {Short(root.Commit)} ({root.SourceDate}). " +
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
    internal static bool IsSafeName(string name) =>
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

    private static List<ScriptSample> ReadFolder(string folder) =>
        IsClone(folder) ? ReadClone(folder) : ReadScripts(folder);

    /// <summary>A pnp/script-samples checkout, read from its per-sample assets/sample.json.</summary>
    private static List<ScriptSample> ReadClone(string root)
    {
        var scripts = Path.Combine(root, "scripts");
        if (!Directory.Exists(scripts))
        {
            return [];
        }

        var samples = new List<ScriptSample>();

        foreach (var dir in Directory.EnumerateDirectories(scripts))
        {
            var manifest = Path.Combine(dir, "assets", "sample.json");
            if (!File.Exists(manifest) || !IsLinkFree(root, manifest))
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
                        // Templated when the manifest omits it, so every source yields a reference URL.
                        Url = Text(element, "url") is { Length: > 0 } url
                            ? url
                            : $"https://pnp.github.io/script-samples/{name}/README.html",
                        RawUrl = $"https://raw.githubusercontent.com/pnp/script-samples/main/scripts/{name}/README.md",
                        Tags = element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                            ? [.. tags.EnumerateArray().Select(t => t.GetString() ?? string.Empty)]
                            : [],
                        LocalRoot = root,
                    });
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // A malformed or wrongly typed manifest skips that sample rather than the whole checkout.
            }
        }

        // Restated because this path does not run through Normalize.
        samples.RemoveAll(s => !IsSafeName(s.Name));

        return samples;
    }

    /// <summary>Any folder of .ps1 files, each described by its comment-based help.</summary>
    private static List<ScriptSample> ReadScripts(string folder)
    {
        var scripts = new FileSystemEnumerable<FileInfo>(
            folder,
            (ref entry) => (FileInfo)entry.ToFileSystemInfo(),
            new EnumerationOptions { RecurseSubdirectories = true })
        {
            ShouldIncludePredicate = (ref entry) =>
                !entry.IsDirectory && entry.FileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && !IsLink(ref entry),
            ShouldRecursePredicate = (ref entry) => !IsLink(ref entry),
        };

        return [.. scripts
            .Where(f => f.Length <= MaxScriptBytes)
            .Take(MaxLocalScripts)
            .Select(f => FromScript(folder, f))
            .OfType<ScriptSample>()
            .Where(s => IsSafeName(s.Name))];
    }

    /// <summary>A folder link can loop and a file link can point anywhere. OneDrive items are reparse points but not links.</summary>
    private static bool IsLink(ref FileSystemEntry entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.ToFileSystemInfo().LinkTarget is not null;

    /// <summary>Rechecks at read time what enumeration did: no link between <paramref name="root"/> and <paramref name="path"/>.</summary>
    // The root itself is the user's choice, and may sit under a link, as macOS temp folders do.
    internal static bool IsLinkFree(string root, string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            return false;
        }

        var rootLength = Path.TrimEndingDirectorySeparator(root).Length;
        for (var folder = Path.GetDirectoryName(path); folder is not null && folder.Length > rootLength; folder = Path.GetDirectoryName(folder))
        {
            if (new DirectoryInfo(folder).LinkTarget is not null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Null when the file cannot be read, so one bad file skips only itself.</summary>
    private static ScriptSample? FromScript(string folder, FileInfo file)
    {
        string script;
        try
        {
            script = File.ReadAllText(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var synopsis = Help(script, "SYNOPSIS");

        return new ScriptSample
        {
            Name = Slug(Path.ChangeExtension(Path.GetRelativePath(folder, file.FullName), null)),
            Title = synopsis.Length > 0 ? synopsis : Path.GetFileNameWithoutExtension(file.Name),
            Description = Help(script, "DESCRIPTION"),
            Url = new Uri(file.FullName).AbsoluteUri,
            LocalPath = file.FullName,
            LocalRoot = folder,
            Tags = [.. CmdletRegex().Matches(script).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }

    internal static string Slug(string text) => SlugRegex().Replace(text, "-").Trim('-');

    private static string Help(string script, string keyword) =>
        HelpRegex().Matches(script).FirstOrDefault(m => m.Groups["key"].Value.Equals(keyword, StringComparison.OrdinalIgnoreCase)) is { } match
            ? string.Join(' ', match.Groups["body"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            : string.Empty;

    // [ \t] rather than \s: \s spans lines, which backtracks for minutes on a run of blank lines.
    [GeneratedRegex(@"^[ \t]*\.(?<key>SYNOPSIS|DESCRIPTION)[ \t]*\r?$(?<body>.*?)(?=^[ \t]*\.[A-Z]+\b|#>)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HelpRegex();

    [GeneratedRegex(@"\b[a-z]+-pnp\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CmdletRegex();

    [GeneratedRegex(@"[^A-Za-z0-9_.]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string Short(string? commit) =>
        string.IsNullOrWhiteSpace(commit) ? "unknown" : commit[..Math.Min(7, commit.Length)];
}
