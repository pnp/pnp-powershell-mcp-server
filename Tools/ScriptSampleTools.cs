using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Tools;

// MCP Tool class
[McpServerToolType]
internal sealed partial class ScriptSampleTools
{
    // Shared HttpClient — safe for the lifetime of a stdio MCP server process
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Cached index — loaded once per process
    private static List<ScriptSample>? _cachedIndex;

    // AOT-safe compiled regexes for script extraction
    [GeneratedRegex(@"#\s*\[PnP PowerShell\][^\n]*\n[\s\S]*?```powershell([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex PnpPsTabRegex();

    [GeneratedRegex(@"```powershell([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellCodeBlockRegex();

    // Index source resolution: extension first, local-repo fallback

    /// <summary>
    /// Returns the path to samples.json inside the PnP PowerShell VS Code extension,
    /// or null if the extension is not installed.
    /// </summary>
    private static string? FindExtensionSamplesJson()
    {
        var extensionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode", "extensions");

        if (!Directory.Exists(extensionsDir))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(extensionsDir, "adamwojcikit.pnp-powershell-extension-*"))
        {
            var candidate = Path.Combine(dir, "out", "data", "samples.json");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Loads the index.  Priority:
    ///   1. PnP PowerShell VS Code extension's samples.json  (auto-discovered, no config needed)
    ///   2. PNP_SCRIPT_SAMPLES_PATH env var → local repo on disk  (manual fallback)
    /// Returns an empty list + a human-readable error message when neither source is available.
    /// </summary>
    private static (List<ScriptSample> Index, string? ErrorMessage) LoadIndex()
    {
        if (_cachedIndex != null)
            return (_cachedIndex, null);

        //Source 1: VS Code extension
        var extensionJsonPath = FindExtensionSamplesJson();
        if (extensionJsonPath != null)
        {
            try
            {
                var json = File.ReadAllText(extensionJsonPath);
                var root = JsonSerializer.Deserialize(json, ScriptSampleJsonContext.Default.ExtensionSamplesRoot);
                if (root?.Samples is { Count: > 0 } samples)
                {
                    // Derive the slug name from rawUrl for each sample
                    foreach (var s in samples)
                    {
                        if (!string.IsNullOrWhiteSpace(s.RawUrl))
                        {
                            // rawUrl pattern: ...main/scripts/{name}/README.md
                            var segments = s.RawUrl.TrimEnd('/').Split('/');
                            var readmeIdx = Array.IndexOf(segments, "README.md");
                            if (readmeIdx > 0)
                                s.Name = segments[readmeIdx - 1];
                        }
                    }

                    _cachedIndex = samples;
                    return (_cachedIndex, null);
                }
            }
            catch
            {
                // Fall through to local-repo source
            }
        }

        //Source 2: local repo via PNP_SCRIPT_SAMPLES_PATH env var
        var envPath = Environment.GetEnvironmentVariable("PNP_SCRIPT_SAMPLES_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var scriptsDir = Path.Combine(envPath, "scripts");
            if (Directory.Exists(scriptsDir))
            {
                var samples = new List<ScriptSample>();
                foreach (var dir in Directory.EnumerateDirectories(scriptsDir))
                {
                    var sampleJsonPath = Path.Combine(dir, "assets", "sample.json");
                    if (!File.Exists(sampleJsonPath)) continue;
                    try
                    {
                        // Local per-sample JSON has a different shape; parse it as a generic array
                        using var doc = JsonDocument.Parse(File.ReadAllText(sampleJsonPath));
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var sample = new ScriptSample
                            {
                                Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : Path.GetFileName(dir),
                                Title = el.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                                Description = el.TryGetProperty("shortDescription", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                                Url = el.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                            };
                            if (el.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                                sample.Tags = [.. tags.EnumerateArray().Select(x => x.GetString() ?? string.Empty)];
                            samples.Add(sample);
                        }
                    }
                    catch { /* skip bad files */ }
                }
                _cachedIndex = samples;
                return (_cachedIndex, null);
            }
        }

        //Neither source available
        _cachedIndex = [];
        return (_cachedIndex,
            """
            No script sample source was found. The MCP server looks for samples in two places (in order):

            1. **PnP PowerShell VS Code extension** (recommended — no configuration needed)
               Install from the VS Code Marketplace: search for "PnP PowerShell" by Adam Wójcik

            2. **Local pnp/script-samples clone** (fallback)
               Clone https://github.com/pnp/script-samples and set the environment variable:
               PNP_SCRIPT_SAMPLES_PATH=<path-to-cloned-repo>
            """);
    }

    //Relevance scoring — weighted keyword matching across all searchable fields

    private static int ScoreMatch(ScriptSample sample, string[] queryTerms)
    {
        int score = 0;
        foreach (var term in queryTerms)
        {
            if (sample.Title.Contains(term, StringComparison.OrdinalIgnoreCase))       score += 10;
            if (sample.Name.Contains(term, StringComparison.OrdinalIgnoreCase))        score += 8;
            if (sample.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 5;
            foreach (var tag in sample.Tags)
                if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))            score += 6;
        }
        return score;
    }

    //Script extraction — prefers the PnP PowerShell tab, falls back to first block

    private static string ExtractPnpScript(string readmeContent)
    {
        var match = PnpPsTabRegex().Match(readmeContent);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        match = PowerShellCodeBlockRegex().Match(readmeContent);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        return string.Empty;
    }

    /// <summary>Fetches the raw README from GitHub and extracts the PnP PowerShell script block.</summary>
    private static async Task<string> FetchScriptFromRawUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return string.Empty;

        // Only allow fetching from the official pnp/script-samples raw GitHub URL (security: no SSRF)
        if (!rawUrl.StartsWith("https://raw.githubusercontent.com/pnp/script-samples/", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        try
        {
            var readmeContent = await Http.GetStringAsync(rawUrl);
            return ExtractPnpScript(readmeContent);
        }
        catch
        {
            return string.Empty;
        }
    }

    //Tool 1: Search script samples

    [McpServerTool(Name = "pnp_search_script_samples")]
    [Description(
        "Searches the PnP Script Samples index (sourced from the PnP PowerShell VS Code extension) for " +
        "community-contributed PowerShell scripts matching a keyword or use case. Returns titles, descriptions, " +
        "relevant cmdlet tags, and direct reference URLs. Use this tool first to discover relevant samples " +
        "before building a solution from scratch.")]
    public Task<string> SearchScriptSamples(
        [Description("Keywords describing the task or area to search for " +
                     "(e.g., 'document set', 'teams bulk create', 'export list items csv', 'site permissions report', 'hub site')")] string query,
        [Description("Maximum number of results to return (default: 10, max: 50)")] int limit = 10)
    {
        limit = Math.Clamp(limit, 1, 50);

        var (index, error) = LoadIndex();
        if (error != null) return Task.FromResult(error);
        if (index.Count == 0)
            return Task.FromResult("The sample index is empty. Re-install the PnP PowerShell VS Code extension or verify your PNP_SCRIPT_SAMPLES_PATH.");

        var queryTerms = query.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = index
            .Select(s => (Sample: s, Score: ScoreMatch(s, queryTerms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .ToList();

        if (results.Count == 0)
        {
            return Task.FromResult(
                $"No script samples matched '{query}'.\n" +
                "Try broader terms such as: site, list, teams, permissions, export, bulk, user, flow, app, hub.\n" +
                "Browse all 297+ samples at: https://pnp.github.io/script-samples/");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found **{results.Count}** script sample(s) matching '{query}':\n");

        foreach (var (sample, _) in results)
        {
            sb.AppendLine($"## {sample.Title}");
            sb.AppendLine($"- **Name**: `{sample.Name}`");
            if (!string.IsNullOrWhiteSpace(sample.Description))
                sb.AppendLine($"- **Description**: {sample.Description}");
            var cmdlets = sample.Tags.Where(t => t.Contains('-')).Take(6).ToList();
            if (cmdlets.Count > 0)
                sb.AppendLine($"- **Key Cmdlets**: {string.Join(", ", cmdlets)}");
            if (!string.IsNullOrWhiteSpace(sample.Url))
                sb.AppendLine($"- **Reference**: {sample.Url}");
            if (sample.Authors.Count > 0)
                sb.AppendLine($"- **Authors**: {string.Join(", ", sample.Authors.Select(a => a.Name))}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("TIP: Use `pnp_get_script_sample` with a sample **Name** (e.g., `spo-create-documentset`) to retrieve the full script code.");
        sb.AppendLine("TIP: Use `pnp_suggest_script` to search and return full script code in a single call.");

        return Task.FromResult(OutputLimit.Apply(sb.ToString(), "Lower the limit, or search with more specific keywords."));
    }

    //Tool 2: Get full script for a named sample (fetches live from GitHub)

    [McpServerTool(Name = "pnp_get_script_sample")]
    [Description(
        "Retrieves the full PnP PowerShell script code for a specific script sample by fetching its README " +
        "directly from the pnp/script-samples GitHub repository. Use this after pnp_search_script_samples " +
        "to get the complete, ready-to-adapt script code.")]
    public async Task<string> GetScriptSample(
        [Description("The sample slug name returned by pnp_search_script_samples " +
                     "(e.g., 'spo-create-documentset', 'teams-bulk-create-teams', 'spo-export-sharepoint-list-items-to-csv')")] string sampleName)
    {
        if (string.IsNullOrWhiteSpace(sampleName))
            return "Error: Please provide a sample name. Use 'pnp_search_script_samples' to discover available sample names.";

        var (index, error) = LoadIndex();
        if (error != null) return error;

        // Find matching sample in the index
        var safeName = sampleName.Trim().ToLowerInvariant();
        var sample = index.FirstOrDefault(s =>
            s.Name.Equals(safeName, StringComparison.OrdinalIgnoreCase) ||
            s.Url.Contains(safeName, StringComparison.OrdinalIgnoreCase));

        if (sample == null)
        {
            return $"Sample '{safeName}' was not found in the index.\n" +
                   "Use 'pnp_search_script_samples' to find the correct sample name.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {sample.Title}");
        if (!string.IsNullOrWhiteSpace(sample.Description))
            sb.AppendLine($"\n{sample.Description}\n");
        if (!string.IsNullOrWhiteSpace(sample.Url))
            sb.AppendLine($"**Reference**: {sample.Url}\n");

        // Fetch script code — try rawUrl (GitHub) first, then local repo fallback
        var scriptCode = string.Empty;

        if (!string.IsNullOrWhiteSpace(sample.RawUrl))
        {
            scriptCode = await FetchScriptFromRawUrl(sample.RawUrl);
        }

        if (string.IsNullOrWhiteSpace(scriptCode))
        {
            // Fallback: local repo on disk
            var envPath = Environment.GetEnvironmentVariable("PNP_SCRIPT_SAMPLES_PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                var localReadme = Path.Combine(envPath, "scripts", sample.Name, "README.md");
                if (File.Exists(localReadme))
                    scriptCode = ExtractPnpScript(await File.ReadAllTextAsync(localReadme));
            }
        }

        if (!string.IsNullOrWhiteSpace(scriptCode))
        {
            sb.AppendLine("## Script (PnP PowerShell)");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine(scriptCode);
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine("⚠️ Script code could not be retrieved automatically.");
            if (!string.IsNullOrWhiteSpace(sample.Url))
                sb.AppendLine($"View the full sample online: {sample.Url}");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("TIP: Replace all placeholder values (tenant URLs, site paths, credentials) with your environment's actual values.");
        sb.AppendLine("TIP: Use `pnp_run_command` to test individual commands incrementally before running the full script.");
        sb.AppendLine("TIP: Use `pnp_get_command_docs` to understand any unfamiliar cmdlets in the script.");

        return OutputLimit.Apply(sb.ToString(), "Open the reference URL above to read the whole sample.");
    }

    //Tool 3: Suggest scripts for a task (search + fetch code in one call)

    [McpServerTool(Name = "pnp_suggest_script")]
    [Description(
        "Finds the most relevant PnP community script samples for a given task and returns their full " +
        "PnP PowerShell script code along with adaptation guidance — all in a single call. " +
        "Script content is fetched live from the pnp/script-samples GitHub repository. " +
        "Use this as your primary starting point when building a new script.")]
    public async Task<string> SuggestScript(
        [Description("A natural-language description of what you want to accomplish with PnP PowerShell. " +
                     "Be as specific as possible (e.g., 'Export all SharePoint list items to a CSV file', " +
                     "'Bulk create Microsoft Teams from a JSON file', " +
                     "'Report on all site collection permissions across the tenant')")] string task,
        [Description("Maximum number of sample scripts to return (default: 3, max: 5)")] int maxSamples = 3)
    {
        maxSamples = Math.Clamp(maxSamples, 1, 5);

        var (index, error) = LoadIndex();
        if (error != null) return error;

        var queryTerms = task.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var topMatches = index
            .Select(s => (Sample: s, Score: ScoreMatch(s, queryTerms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(maxSamples)
            .ToList();

        if (topMatches.Count == 0)
        {
            return $"No matching script samples found for: '{task}'.\n\n" +
                   "Suggestions:\n" +
                   "- Use 'pnp_search_commands' to find relevant PnP PowerShell cmdlets.\n" +
                   "- Use 'pnp_get_command_docs' to get full syntax and examples for specific cmdlets.\n" +
                   "- Browse all samples at: https://pnp.github.io/script-samples/";
        }

        // Fetch all scripts in parallel for speed
        var fetchTasks = topMatches.Select(m => FetchScriptFromRawUrl(m.Sample.RawUrl)).ToList();
        var scripts = await Task.WhenAll(fetchTasks);

        var sb = new StringBuilder();
        sb.AppendLine($"# Script Suggestions for: \"{task}\"");
        sb.AppendLine($"\nFound **{topMatches.Count}** relevant community sample(s).\n");

        for (int i = 0; i < topMatches.Count; i++)
        {
            var (sample, _) = topMatches[i];
            var scriptCode = scripts[i];

            sb.AppendLine("---");
            sb.AppendLine($"## {i + 1}. {sample.Title}");

            if (!string.IsNullOrWhiteSpace(sample.Description))
                sb.AppendLine($"\n> {sample.Description}\n");

            if (!string.IsNullOrWhiteSpace(sample.Url))
                sb.AppendLine($"**Reference**: {sample.Url}");

            var cmdlets = sample.Tags.Where(t => t.Contains('-')).Take(6).ToList();
            if (cmdlets.Count > 0)
                sb.AppendLine($"**Key Cmdlets**: {string.Join(", ", cmdlets)}");

            if (sample.Authors.Count > 0)
                sb.AppendLine($"**Authors**: {string.Join(", ", sample.Authors.Select(a => a.Name))}");

            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(scriptCode))
            {
                sb.AppendLine("```powershell");
                sb.AppendLine(scriptCode);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine($"*(Script could not be fetched automatically — view online: {sample.Url})*");
            }

            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("## How to adapt these scripts");
        sb.AppendLine("1. **Choose** the closest matching sample above.");
        sb.AppendLine("2. **Replace** all placeholder values: tenant URLs, site paths, list names, and credentials.");
        sb.AppendLine("3. **Test incrementally**: use `pnp_run_command` to run individual commands before executing the full script.");
        sb.AppendLine("4. **Understand cmdlets**: use `pnp_get_command_docs` for any cmdlet you are unfamiliar with.");
        sb.AppendLine("5. **Combine**: if no single sample covers your scenario, merge relevant sections from multiple scripts.");

        return OutputLimit.Apply(sb.ToString(), "Lower maxSamples, or fetch one sample at a time with 'pnp_get_script_sample'.");
    }
}
