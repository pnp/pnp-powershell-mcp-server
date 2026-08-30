using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Tools;

[McpServerToolType]
internal sealed partial class ScriptSampleTools
{
    // Shared HttpClient — safe for the lifetime of a stdio MCP server process
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    [GeneratedRegex(@"#\s*\[PnP PowerShell\][^\n]*\n[\s\S]*?```powershell([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex PnpPsTabRegex();

    [GeneratedRegex(@"```powershell([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellCodeBlockRegex();

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

    private static List<ScriptSample> Rank(string query, int limit)
    {
        var terms = Terms(query);

        return
        [.. ScriptSampleIndex.Samples
            .Select(s => (Sample: s, Score: ScoreMatch(s, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Sample)];
    }

    private static string[] Terms(string query) =>
        (query ?? string.Empty).Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ExtractPnpScript(string readmeContent)
    {
        var match = PnpPsTabRegex().Match(readmeContent);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        match = PowerShellCodeBlockRegex().Match(readmeContent);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>Fetches the README and extracts the script block.</summary>
    // Enrichment only: a failed fetch degrades to the reference URL.
    private static async Task<string> FetchScript(ScriptSample sample, CancellationToken cancellationToken)
    {
        var local = Environment.GetEnvironmentVariable("PNP_SCRIPT_SAMPLES_PATH");
        if (!string.IsNullOrWhiteSpace(local))
        {
            var readme = Path.Combine(local, "scripts", sample.Name, "README.md");
            if (File.Exists(readme))
                return ExtractPnpScript(await File.ReadAllTextAsync(readme, cancellationToken));
        }

        // Only the official raw GitHub URL is fetchable, so a tampered index cannot redirect us.
        if (!sample.RawUrl.StartsWith("https://raw.githubusercontent.com/pnp/script-samples/", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        try
        {
            return ExtractPnpScript(await Http.GetStringAsync(sample.RawUrl, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return string.Empty;
        }
    }

    private static void AppendSampleFacts(StringBuilder sb, ScriptSample sample, string bullet)
    {
        if (!string.IsNullOrWhiteSpace(sample.Description))
            sb.AppendLine($"{bullet}**Description**: {sample.Description}");

        var cmdlets = sample.Tags.Where(t => t.Contains('-')).Take(6).ToList();
        if (cmdlets.Count > 0)
            sb.AppendLine($"{bullet}**Key Cmdlets**: {string.Join(", ", cmdlets)}");

        if (!string.IsNullOrWhiteSpace(sample.Url))
            sb.AppendLine($"{bullet}**Reference**: {sample.Url}");

        if (sample.Authors.Count > 0)
            sb.AppendLine($"{bullet}**Authors**: {string.Join(", ", sample.Authors.Select(a => a.Name))}");
    }

    /// <summary>Index provenance, as a suffix so the output cap cannot drop it.</summary>
    private static string Provenance => "\n\n" + ScriptSampleIndex.Provenance;

    private static string NoMatch(string query) =>
        $"No script samples matched '{OutputLimit.Echo(query)}'.\n" +
        "Try broader terms such as: site, list, teams, permissions, export, bulk, user, flow, app, hub.\n" +
        $"Browse the whole catalogue at https://pnp.github.io/script-samples/\n\n{ScriptSampleIndex.Provenance}";

    [McpServerTool(
        Name = "pnp_search_script_samples",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(SampleSearchResult))]
    [Description(
        "Browses the catalogue of community PnP Script Samples by keyword and returns titles, descriptions and " +
        "reference links only, never any code. Use it to see what community solutions already exist in an area.")]
    public static CallToolResult SearchScriptSamples(
        [Description("Keywords describing the task or area to browse for " +
                     "(e.g., 'document set', 'teams bulk create', 'export list items csv', 'site permissions report', 'hub site')")] string query,
        [Description("Maximum number of results to return (default: 10, max: 50)")] int limit = 10)
    {
        var matched = Rank(query, Math.Clamp(limit, 1, 50));

        return StructuredResult.FitToCap(
            matched,
            (page, _) => new SampleSearchResult
            {
                Query = OutputLimit.Echo(query),
                Matched = matched.Count,
                Samples = [.. page.Select(s => new SampleHit { Name = s.Name, Title = s.Title, Url = s.Url })],
            },
            ToolOutputJsonContext.Default.SampleSearchResult,
            result => RenderSamples(result, matched));
    }

    /// <param name="matched">The full match set, so the rendered facts survive the structured projection.</param>
    private static string RenderSamples(SampleSearchResult result, IReadOnlyList<ScriptSample> matched)
    {
        if (result.Count == 0)
        {
            return NoMatch(result.Query);
        }

        var sb = new StringBuilder();
        sb.AppendLine(result.Truncated
            ? $"Found **{result.Matched}** script sample(s) matching '{result.Query}', showing the first {result.Count}:\n"
            : $"Found **{result.Count}** script sample(s) matching '{result.Query}':\n");

        foreach (var sample in result.Samples.Select(hit => matched.First(m => m.Name == hit.Name)))
        {
            sb.AppendLine($"## {sample.Title}");
            sb.AppendLine($"- **Name**: `{sample.Name}`");
            AppendSampleFacts(sb, sample, "- ");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("TIP: Use `pnp_get_script_sample` with a sample **Name** (e.g., `spo-create-documentset`) to retrieve the full script code.");

        return OutputLimit.Apply(
            sb.ToString(),
            "Pass a smaller 'limit' to return fewer samples, or search with more specific keywords.",
            Provenance);
    }

    [McpServerTool(Name = "pnp_get_script_sample", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Opens one specific sample already identified, addressed by the exact slug name a browse returned, and " +
        "gives back its complete code. Use it when you know precisely which sample you want.")]
    public static async Task<string> GetScriptSample(
        [Description("The sample slug name returned by pnp_search_script_samples " +
                     "(e.g., 'spo-create-documentset', 'teams-bulk-create-teams', 'spo-export-sharepoint-list-items-to-csv')")] string sampleName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sampleName))
            return "Error: Please provide a sample name. Use 'pnp_search_script_samples' to discover available sample names.";

        var wanted = sampleName.Trim();
        var sample = ScriptSampleIndex.Samples.FirstOrDefault(s =>
            s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
            (s.Url.Length > 0 && s.Url.Contains(wanted, StringComparison.OrdinalIgnoreCase)));

        if (sample is null)
            return $"Sample '{OutputLimit.Echo(wanted)}' was not found in the index.\n" +
                   $"Use 'pnp_search_script_samples' to find the correct sample name.\n\n{ScriptSampleIndex.Provenance}";

        var sb = new StringBuilder();
        sb.AppendLine($"# {sample.Title}");
        sb.AppendLine();
        AppendSampleFacts(sb, sample, "- ");
        sb.AppendLine();

        var scriptCode = await FetchScript(sample, cancellationToken);

        if (scriptCode.Length > 0)
        {
            sb.AppendLine("## Script (PnP PowerShell)");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine(scriptCode);
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine(sample.Url.Length > 0
                ? "The script body could not be fetched, so only the index entry is shown. Read it at the reference above."
                : "The script body could not be fetched, and this index entry carries no reference URL.");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("TIP: Replace all placeholder values (tenant URLs, site paths, credentials) with your environment's actual values.");
        sb.AppendLine("TIP: Use `pnp_run_command` to test individual commands incrementally before running the full script.");

        return OutputLimit.Apply(sb.ToString(), "Open the reference URL above to read the whole sample.", Provenance);
    }

    [McpServerTool(Name = "pnp_suggest_script", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Drafts a starting point for a job someone wants to automate, by matching the task against community " +
        "samples and returning their code plus guidance on adapting it. The entry point when a job is " +
        "described rather than a cmdlet named.")]
    public static async Task<string> SuggestScript(
        [Description("A natural-language description of what you want to accomplish with PnP PowerShell. " +
                     "Be as specific as possible (e.g., 'Export all SharePoint list items to a CSV file', " +
                     "'Bulk create Microsoft Teams from a JSON file', " +
                     "'Report on all site collection permissions across the tenant')")] string task,
        [Description("Maximum number of sample scripts to return (default: 3, max: 5)")] int maxSamples = 3,
        CancellationToken cancellationToken = default)
    {
        var matches = Rank(task, Math.Clamp(maxSamples, 1, 5));

        if (matches.Count == 0)
            return NoMatch(task) + "\n\nAlso try 'pnp_search_commands' to find the cmdlets for this task and build the script from their documentation.";

        var scripts = await Task.WhenAll(matches.Select(m => FetchScript(m, cancellationToken)));

        var sb = new StringBuilder();
        sb.AppendLine($"# Script Suggestions for: \"{OutputLimit.Echo(task)}\"");
        sb.AppendLine($"\nFound **{matches.Count}** relevant community sample(s).\n");

        for (int i = 0; i < matches.Count; i++)
        {
            sb.AppendLine("---");
            sb.AppendLine($"## {i + 1}. {matches[i].Title}");
            AppendSampleFacts(sb, matches[i], "- ");
            sb.AppendLine();

            if (scripts[i].Length > 0)
            {
                sb.AppendLine("```powershell");
                sb.AppendLine(scripts[i]);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine($"*(Script body could not be fetched — read it at {matches[i].Url})*");
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

        return OutputLimit.Apply(
            sb.ToString(),
            "Lower maxSamples, or fetch one sample at a time with 'pnp_get_script_sample'.",
            Provenance);
    }
}
