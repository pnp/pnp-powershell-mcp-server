using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Services;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Tools;

[McpServerToolType]
internal partial class PnPPowerShellTools
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Textual destructive-verb check, run alongside the parsed check in <see cref="CommandPolicy"/>.</summary>
    // Not limited to -PnP cmdlets: reaching this tool only needs -PnP somewhere in the script, so a
    // destructive non-PnP command riding along ("Remove-Item -Recurse -Force ...; Get-PnPWeb") counts too.
    [GeneratedRegex(@"\b(Remove|Clear|Reset|Uninstall|Revoke|Deny|Restore|Move|Rename|Disable)-[A-Za-z]\w*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DestructiveCommandRegex();

    /// <summary>Per-command wall-clock limit; generous because long tenant-wide operations are normal here.</summary>
    private static TimeSpan CommandTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable("PNP_MCP_COMMAND_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(10);

    private static bool ConfirmationDisabled =>
        string.Equals(Environment.GetEnvironmentVariable("PNP_MCP_CONFIRM_DESTRUCTIVE"), "false", StringComparison.OrdinalIgnoreCase);

    [McpServerTool(Name = "pnp_search_commands", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches PnP PowerShell commands using keyword matching against command names, verbs, and nouns. Use this tool first to find relevant commands before getting full command documentation.")]
    public static async Task<string> SearchPnpCommands(
        PowerShellSessionManager sessions,
        [Description("One or more space-separated keywords to find relevant commands (e.g., \"site\", \"list item\", \"teams channel\", \"user add\")")] string query,
        [Description("Maximum number of results to return (default: 20, max: 100)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        var terms = (query ?? string.Empty)
            .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EscapeSingleQuotedPowerShell)
            .ToArray();

        if (terms.Length == 0)
        {
            return "Error: Please provide at least one search keyword.";
        }

        var termArray = "@(" + string.Join(",", terms.Select(t => $"'{t}'")) + ")";

        var script = $$"""
            $__pnpTerms = {{termArray}}
            Get-Command -Module PnP.PowerShell |
              ForEach-Object {
                $__pnpCmd = $_
                $__pnpScore = 0
                foreach ($__pnpTerm in $__pnpTerms) {
                  if ($__pnpCmd.Name -like "*$__pnpTerm*") { $__pnpScore += 10 }
                  if ($__pnpCmd.Verb -like "*$__pnpTerm*") { $__pnpScore += 4 }
                  if ($__pnpCmd.Noun -like "*$__pnpTerm*") { $__pnpScore += 6 }
                }
                if ($__pnpScore -gt 0) {
                  [PSCustomObject]@{ Name = $__pnpCmd.Name; Verb = $__pnpCmd.Verb; Noun = $__pnpCmd.Noun; HelpUri = $__pnpCmd.HelpUri; Score = $__pnpScore }
                }
              } |
              Sort-Object -Property Score -Descending |
              Select-Object -First {{limit}} Name, Verb, Noun, HelpUri |
              ConvertTo-Json -Depth 5 -Compress
            Remove-Variable -Name __pnpTerms, __pnpCmd, __pnpScore, __pnpTerm -ErrorAction SilentlyContinue
            """;

        var result = await sessions.Get(null).ExecuteAsync(script, MetadataTimeout, cancellationToken);

        const string searchTips = """


            TIP: Before executing any of the commands, run the 'pnp_get_command_docs' tool to retrieve the full syntax, parameters, and examples.
            TIP: Each result carries a HelpUri, the published documentation page for that cmdlet. Fetch it when you need more detail than the local help gives, or cite it to the user.
            TIP: For complex tasks, break them into smaller steps and run commands incrementally using 'pnp_run_command'.
            """;

        // The TIPs are passed as a suffix so they count against the cap instead of being appended past it.
        return OutputLimit.Apply(
            result,
            "Search with fewer keywords, or pass a smaller 'limit' to return fewer results.",
            PnPErrorHints.HintFor(result) ?? searchTips);
    }

    [McpServerTool(Name = "pnp_get_command_docs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets detailed documentation for a specific PnP PowerShell command including syntax, parameters, and examples. Use this after searching for commands to understand how to use them correctly.")]
    public static async Task<string> GetPnpCommandDocs(
        PowerShellSessionManager sessions,
        [Description("The full PnP PowerShell command name (e.g., \"Get-PnPWeb\", \"Connect-PnPOnline\", \"Get-PnPList\")")] string commandName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return "Error: No command name provided. Use 'pnp_search_commands' to find one, then pass its full name (e.g., \"Get-PnPWeb\").";
        }

        var safeCommandName = EscapeSingleQuotedPowerShell(commandName.Trim());

        var script = $$"""
            $__pnpName = '{{safeCommandName}}'
            $__pnpHelpText = Get-Help $__pnpName -Full | Out-String
            if ([string]::IsNullOrWhiteSpace($__pnpHelpText)) {
              Write-Output "No documentation found for '$__pnpName'. Verify the command name using 'pnp_search_commands'."
            } else {
              # The link goes first, not last: help for some cmdlets runs to six figures of characters
              # (Set-PnPTenant is ~135k), so a trailing link is exactly what the output cap would drop --
              # and a cmdlet with help that long is the one most likely to need the online page.
              $__pnpHelpUri = $null
              try { $__pnpHelpUri = ($ExecutionContext.InvokeCommand.GetCommand($__pnpName, [System.Management.Automation.CommandTypes]::All)).HelpUri } catch { $__pnpHelpUri = $null }
              if (-not [string]::IsNullOrWhiteSpace($__pnpHelpUri)) {
                Write-Output "ONLINE DOCUMENTATION: $__pnpHelpUri"
                Write-Output "TIP: If the syntax or examples below look incomplete, fetch that page with your web-fetch tool -- it is generated from the current source and usually carries more parameter detail and examples than the shipped help. If you cannot fetch pages, give the user the link instead."
              } else {
                Write-Output "NOTE: This cmdlet reports no documentation URL, which usually means an older PnP.PowerShell build (HelpUri is populated in current versions)."
                Write-Output "FALLBACK: Search https://pnp.github.io/powershell/ for '$__pnpName' to find its page, or search the web for 'PnP PowerShell $__pnpName'. Do not hand-assemble a docs URL -- the path pattern is not guaranteed. Updating the module with 'Update-Module PnP.PowerShell' also restores the link."
              }
              Write-Output ''
              Write-Output $__pnpHelpText
            }
            Remove-Variable -Name __pnpName, __pnpHelpText, __pnpHelpUri -ErrorAction SilentlyContinue
            """;

        var help = await sessions.Get(null).ExecuteAsync(script, MetadataTimeout, cancellationToken);

        return OutputLimit.Apply(help, "Read the online documentation page linked above for the full reference.", PnPErrorHints.HintFor(help));
    }

    [McpServerTool(Name = "pnp_run_command", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Executes one or more PnP PowerShell commands and returns the result. Commands can be chained with semicolons or newlines. The connection established by Connect-PnPOnline persists across calls that use the same sessionId, so you only need to connect once. This tool can be used repeatedly to accomplish complex multi-step tasks.")]
    public static async Task<string> RunPnpCommand(
        PowerShellSessionManager sessions,
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("PnP PowerShell command(s) to execute (e.g., \"Get-PnPSite\", \"Get-PnPList | Select-Object Title, ItemCount\")")] string command,
        [Description("Session to run in. Sessions keep their own PnP connection, so use a second name only when working against two tenants at once (default: \"default\")")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Error: No command provided. Please specify a PnP PowerShell command to execute.";
        }

        if (!LooksLikePnpCommand(command))
        {
            return """
                Error: The command does not appear to contain a PnP PowerShell cmdlet.
                PnP PowerShell commands follow the pattern: Verb-PnPNoun (e.g., Get-PnPWeb, Set-PnPList, Add-PnPListItem).
                Use 'pnp_search_commands' to find the correct command name.
                """;
        }

        var session = sessions.Get(sessionId);

        // Analysis and execution share one budget, so queuing behind a long command still waits out
        // CommandTimeout rather than failing early, and a call cannot exceed the configured limit.
        var budget = CommandTimeout;
        var (analysisBudget, executionFloor) = SplitBudget(budget);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        // Parsed rather than pattern-matched, so aliases and indirect invocation are seen for what they are.
        var (analysis, sessionError) = await ScriptAnalyzer.AnalyzeAsync(session, command, analysisBudget, cancellationToken);

        if (sessionError is not null)
        {
            return PnPErrorHints.Enrich(sessionError);
        }

        if (analysis is not null)
        {
            var blocked = CommandPolicy.Enforce(analysis);
            if (blocked is not null)
            {
                return blocked;
            }
        }
        else if (CommandPolicy.ReadOnlyMode)
        {
            // Fail closed: with no analysis there is no evidence the script only reads.
            return
                "Blocked: read-only mode is on (PNP_MCP_READONLY=true) but this command could not be analysed, " +
                "so it was not run. Simplify the command and try again.";
        }

        var flagged = DetermineConfirmationTarget(analysis, command);
        if (flagged is not null && !ConfirmationDisabled)
        {
            var refusal = await ConfirmDestructiveAsync(server, context, command, flagged);
            if (refusal is not null)
            {
                return refusal;
            }
        }

        // Base64-encode the command so quoting inside it cannot break the wrapper.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        // Wrapper variables are __pnp-prefixed and removed afterwards: the session is shared, so a plain
        // name like $result would overwrite the caller's own variable between calls.
        var script = $$"""
            $__pnpCommandText = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encoded}}'))
            $__pnpCommandResult = Invoke-Expression $__pnpCommandText
            if ($null -ne $__pnpCommandResult) {
              try {
                $__pnpCommandResult | ConvertTo-Json -Depth 20 -Compress
              }
              catch {
                $__pnpCommandResult | Out-String
              }
            } else {
              Write-Output 'Command completed successfully (no output).'
            }
            Remove-Variable -Name __pnpCommandText, __pnpCommandResult -ErrorAction SilentlyContinue
            """;

        // Whatever the analysis consumed is deducted, never dropping below the slice reserved for it.
        var remaining = budget - elapsed.Elapsed;
        if (remaining < executionFloor)
        {
            remaining = executionFloor;
        }

        var result = await session.ExecuteAsync(script, remaining, cancellationToken);

        // The hint is reserved as a suffix rather than appended after capping, so the response stays
        // inside PNP_MCP_MAX_OUTPUT_CHARS and the "Likely cause" line still survives a truncation.
        return OutputLimit.Apply(result, suffix: PnPErrorHints.HintFor(result));
    }

    /// <summary>Splits a command budget into an analysis cap and a reserved execution slice.</summary>
    // The execution floor is reserved up front rather than added afterwards. Granting analysis the whole
    // budget and then flooring execution at a fixed 10s let a slow analysis push the total past the
    // configured timeout; capping analysis at budget-minus-floor keeps the sum within it.
    internal static (TimeSpan Analysis, TimeSpan ExecutionFloor) SplitBudget(TimeSpan budget)
    {
        // Halve a very short budget instead of reserving a fixed slice, which would leave nothing to analyse with.
        var floor = budget < TimeSpan.FromSeconds(20) ? budget / 2 : TimeSpan.FromSeconds(10);

        return (budget - floor, floor);
    }

    /// <summary>Describes what must be confirmed before running, or null when nothing must be.</summary>
    internal static string? DetermineConfirmationTarget(ScriptAnalysis? analysis, string command)
    {
        // Read-only mode needs no prompt: anything that survived Enforce was parsed and proven not to
        // change Microsoft 365, and the textual check's false positives must not leak into a mode that
        // already verified the script.
        if (CommandPolicy.ReadOnlyMode)
        {
            return null;
        }

        // Fail closed when the parse is unavailable. The textual check alone cannot see an alias, an
        // indirect invocation or a CSOM method call, so relying on it here would let exactly the cases
        // the parser exists to catch run unconfirmed.
        if (analysis is null)
        {
            return "a command that could not be analysed, so what it would run cannot be verified";
        }

        // Both checks run: a needless prompt costs a click, a missed one costs tenant data.
        var parsed = CommandPolicy.FindNeedingConfirmation(analysis);
        if (parsed is not null)
        {
            return parsed;
        }

        // Textual fallback catches a destructive name used only as an argument, e.g. Set-Alias nuke Remove-PnPTenantSite.
        var textual = DestructiveCommandRegex().Match(command);
        return textual.Success ? textual.Value : null;
    }

    /// <summary>Null when the command is approved, otherwise the message to return to the caller.</summary>
    private static async Task<string?> ConfirmDestructiveAsync(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        string command,
        string matchedCmdlet)
    {
        // A retry carries the answer to the prompt raised by the previous attempt.
        if (context.Params?.InputResponses?.TryGetValue("confirmDestructive", out var response) is true)
        {
            return EvaluateApproval(
                context.Params.RequestState,
                command,
                response.Deserialize(InputResponse.ElicitResultJsonTypeInfo),
                matchedCmdlet);
        }

        // IsMrtrSupported only says the round-trip can be represented; on the legacy bridge the client
        // must additionally be able to elicit, or the SDK fails the call with an opaque error instead
        // of letting us fall back.
        if (server.IsMrtrSupported && server.ClientCapabilities?.Elicitation is not null)
        {
            throw new InputRequiredException(
                inputRequests: new Dictionary<string, InputRequest>
                {
                    ["confirmDestructive"] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message =
                            $"This command needs confirmation before it runs against the connected tenant.\n\nFlagged: {matchedCmdlet}\n\n{command}\n\nThis may not be reversible. Continue?",
                        RequestedSchema = new()
                        {
                            Required = ["confirm"],
                            Properties =
                            {
                                ["confirm"] = new ElicitRequestParams.BooleanSchema
                                {
                                    Title = "Run this command",
                                    Description = $"Confirm execution of {matchedCmdlet}",
                                    Default = false,
                                },
                            },
                        },
                    }),
                },
                requestState: Fingerprint(command));
        }

        return $"""
            Blocked: this command needs confirmation and this client cannot prompt for it. Nothing was run.

            Flagged: {matchedCmdlet}

            Command:
            {command}

            This cannot be approved from inside the conversation. Show the command to the user and tell them to either run it themselves in a PowerShell session, or switch to a client that supports MCP elicitation so the confirmation prompt can be shown.
            An operator who has already reviewed what this server will be asked to run can set PNP_MCP_CONFIRM_DESTRUCTIVE=false to turn the gate off for the whole process.
            """;
    }

    [McpServerTool(Name = "pnp_diagnose_connection", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Checks everything that has to be true before a PnP PowerShell command can run: that pwsh is installed and on PATH, that the PnP.PowerShell module is present, and what connection this session holds. Every failing check names its cause and the exact next command to run. Call this first when a command fails for a reason you cannot explain, or when starting from an unknown machine.")]
    public static async Task<string> DiagnosePnpConnection(
        PowerShellSessionManager sessions,
        [Description("Session to inspect (default: \"default\")")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var facts = await ConnectionPreflight.GatherAsync(sessions, sessionId, cancellationToken);

        return OutputLimit.Apply(
            ConnectionPreflight.Render(facts),
            "Raise PNP_MCP_MAX_OUTPUT_CHARS to see the whole report; this one is a fixed set of checks, so there is nothing to narrow.");
    }

    [McpServerTool(Name = "pnp_get_connection_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Checks the current PnP PowerShell connection status for a session. Use this to verify if you are already connected to a SharePoint site or Microsoft 365 tenant before running commands.")]
    public static async Task<string> GetPnpConnectionStatus(
        PowerShellSessionManager sessions,
        [Description("Session to inspect (default: \"default\")")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        const string script = """
            try {
              $__pnpConn = Get-PnPConnection
              $__pnpInfo = [ordered]@{
                connected = $true
                url = $__pnpConn.Url
                tenantAdminUrl = $__pnpConn.TenantAdminUrl
                connectionType = $__pnpConn.ConnectionType.ToString()
                account = if ($__pnpConn.PSCredential) { $__pnpConn.PSCredential.UserName } else { $null }
              }
              $__pnpInfo | ConvertTo-Json -Depth 5 -Compress
            }
            catch {
              Write-Output '{"connected":false,"message":"Not connected. Use Connect-PnPOnline to establish a connection. Run pnp_get_command_docs with commandName Connect-PnPOnline for usage details."}'
            }
            Remove-Variable -Name __pnpConn, __pnpInfo -ErrorAction SilentlyContinue
            """;

        var result = await sessions.Get(sessionId).ExecuteAsync(script, MetadataTimeout, cancellationToken);

        return $"""
            Session: {(string.IsNullOrWhiteSpace(sessionId) ? PowerShellSessionManager.DefaultSessionId : sessionId.Trim())}

            {result}
            """ + PnPErrorHints.HintFor(result);
    }

    [McpServerTool(Name = "pnp_reset_session", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Ends a PowerShell session and its PnP connection, discarding all in-session state. Use this to sign out, to recover a session that has stopped responding, or to switch the connected account.")]
    public static async Task<string> ResetPnpSession(
        PowerShellSessionManager sessions,
        [Description("Session to end (default: \"default\")")] string? sessionId = null)
    {
        var name = string.IsNullOrWhiteSpace(sessionId) ? PowerShellSessionManager.DefaultSessionId : sessionId.Trim();
        var existed = await sessions.ResetAsync(sessionId);

        var active = sessions.Describe();
        var summary = active.Count == 0
            ? "No sessions are currently running."
            : "Sessions: " + string.Join(", ", active.Select(s => $"{s.Id} ({(s.IsAlive ? "running" : "stopped")})"));

        return existed
            ? $"Session '{name}' was ended. The next command will start a fresh session and will need to reconnect with Connect-PnPOnline.\n\n{summary}"
            : $"No session named '{name}' was running, so there was nothing to end.\n\n{summary}";
    }

    /// <summary>Named slices of the guidance, so a caller can pull one topic instead of the whole document.</summary>
    // Values are matched against the "## " headings in best-practices.md by exact title, case-insensitively.
    // Internal so a test can assert the [Description] list and the shipped guidance stay in step; the
    // attribute needs a compile-time constant, so the list cannot be generated from this dictionary.
    internal static readonly Dictionary<string, string[]> BestPracticeSections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["workflow"] = ["Recommended Workflow", "Prerequisites", "Summary"],
        ["docs"] = ["Finding More About a Cmdlet"],
        ["sessions"] = ["Sessions"],
        ["config"] = ["Server Configuration"],
        ["readonly"] = ["Read-Only Mode"],
        ["destructive"] = ["Destructive Commands"],
        ["auth"] = ["Authentication Best Practices"],
        ["execution"] = ["Execution Best Practices", "Working with Complex Data", "Debugging and Verbose Output"],
        ["output"] = ["Output Size"],
        ["patterns"] = ["Common Patterns", "Areas Covered by PnP PowerShell"],
    };

    [McpServerTool(Name = "pnp_get_best_practices", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns best practices for using this MCP server with PnP PowerShell. The full document is long, so pass a section to retrieve only what you need.")]
    public static string GetPnpBestPractices(
        [Description("Optional topic to return instead of the whole document. One of: workflow, docs, sessions, config, readonly, output, destructive, auth, execution, patterns. Omit for everything.")] string? section = null)
    {
        var document = BestPracticesDocument.Value;

        if (string.IsNullOrWhiteSpace(section))
        {
            return $"""
                {document}

                TIP: This is the full guide. To pull a single topic next time, call 'pnp_get_best_practices' with section set to one of: {string.Join(", ", BestPracticeSections.Keys)}.
                """;
        }

        var key = section.Trim();
        if (!BestPracticeSections.TryGetValue(key, out var headings))
        {
            return
                $"Error: Unknown section '{key}'. Valid sections are: {string.Join(", ", BestPracticeSections.Keys)}. " +
                "Omit the section to get the whole document.";
        }

        var extracted = ExtractSections(document, headings);

        return string.IsNullOrWhiteSpace(extracted)
            ? $"Error: Section '{key}' is not present in this build of the guidance. Omit the section to get the whole document."
            : extracted.TrimEnd();
    }

    // best-practices.md is compiled into the assembly, so there is exactly one copy of the guidance.
    // It previously fell back to a hand-maintained inline duplicate, which had already drifted: the
    // duplicate used different headings, so the section lookup silently returned nothing for some keys.
    private static readonly Lazy<string> BestPracticesDocument = new(() =>
    {
        using var stream = typeof(PnPPowerShellTools).Assembly.GetManifestResourceStream("best-practices.md")
            ?? throw new InvalidOperationException("best-practices.md is missing from the assembly; it must be an EmbeddedResource.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Returns the named "## " sections of a markdown document, in document order.</summary>
    // Matches on the heading text so the slices keep working as the document is edited, and takes only
    // level-2 headings so a "###" subheading cannot end a section early.
    internal static string ExtractSections(string document, string[] headings)
    {
        var result = new StringBuilder();
        var keeping = false;

        foreach (var line in document.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                var title = trimmed[3..].Trim();
                keeping = headings.Any(h => title.Equals(h, StringComparison.OrdinalIgnoreCase));
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                keeping = false;
            }

            if (keeping)
            {
                result.AppendLine(trimmed);
            }
        }

        return result.ToString();
    }

    /// <summary>Null when the retry is genuinely approved, otherwise the refusal to return.</summary>
    internal static string? EvaluateApproval(string? requestState, string command, ElicitResult? elicited, string matchedCmdlet)
    {
        if (!IsApprovalBoundTo(requestState, command))
        {
            return
                "Cancelled: this approval was not issued for this exact command, so nothing was run. " +
                "Re-run 'pnp_run_command' and confirm the command you are shown.";
        }

        if (elicited?.IsAccepted is not true)
        {
            return $"Cancelled: {matchedCmdlet} was not confirmed, so nothing was run.";
        }

        var confirmed = elicited.Content?.TryGetValue("confirm", out var confirmValue) is true &&
                        confirmValue.ValueKind is JsonValueKind.True;

        return confirmed
            ? null
            : $"Cancelled: {matchedCmdlet} was not explicitly confirmed, so nothing was run. Ask again and have the user tick the confirmation box.";
    }

    /// <summary>Per-process key, so an approval cannot be minted anywhere but here.</summary>
    private static readonly byte[] ApprovalKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Identifies the exact command an approval covers, keyed so a caller cannot forge one.</summary>
    internal static string Fingerprint(string command) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(ApprovalKey, Encoding.UTF8.GetBytes(command)));

    /// <summary>True only when the echoed request state was minted by this process for this command.</summary>
    internal static bool IsApprovalBoundTo(string? requestState, string command) =>
        requestState is not null &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(requestState),
            Encoding.UTF8.GetBytes(Fingerprint(command)));

    private static bool LooksLikePnpCommand(string command)
    {
        return command.Contains("-PnP", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeSingleQuotedPowerShell(string value)
    {
        return value.Replace("'", "''");
    }
}
