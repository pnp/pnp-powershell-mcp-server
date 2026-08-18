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

    [McpServerTool(Name = "pnp_search_commands", ReadOnly = true, OpenWorld = false)]
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

        return $"""
            {result}

            TIP: Before executing any of the commands, run the 'pnp_get_command_docs' tool to retrieve the full syntax, parameters, and examples.
            TIP: Each result carries a HelpUri, the published documentation page for that cmdlet. Fetch it when you need more detail than the local help gives, or cite it to the user.
            TIP: For complex tasks, break them into smaller steps and run commands incrementally using 'pnp_run_command'.
            """;
    }

    [McpServerTool(Name = "pnp_get_command_docs", ReadOnly = true, OpenWorld = false)]
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
            $__pnpHelpText = Get-Help '{{safeCommandName}}' -Full | Out-String
            if ([string]::IsNullOrWhiteSpace($__pnpHelpText)) {
              Write-Output "No documentation found for '{{safeCommandName}}'. Verify the command name using 'pnp_search_commands'."
            } else {
              Write-Output $__pnpHelpText
              # HelpUri points at the published docs, which carry examples the shipped help often omits.
              $__pnpHelpUri = $null
              try { $__pnpHelpUri = ($ExecutionContext.InvokeCommand.GetCommand('{{safeCommandName}}', [System.Management.Automation.CommandTypes]::All)).HelpUri } catch { $__pnpHelpUri = $null }
              if (-not [string]::IsNullOrWhiteSpace($__pnpHelpUri)) {
                Write-Output ''
                Write-Output "ONLINE DOCUMENTATION: $__pnpHelpUri"
                Write-Output "TIP: If the syntax or examples above look incomplete, fetch that page with your web-fetch tool -- it is generated from the current source and usually carries more parameter detail and examples than the shipped help. If you cannot fetch pages, give the user the link instead."
              } else {
                Write-Output ''
                Write-Output "NOTE: This cmdlet reports no documentation URL, which usually means an older PnP.PowerShell build (HelpUri is populated in current versions)."
                Write-Output "FALLBACK: Search https://pnp.github.io/powershell/ for '{{safeCommandName}}' to find its page, or search the web for 'PnP PowerShell {{safeCommandName}}'. Do not hand-assemble a docs URL -- the path pattern is not guaranteed. Updating the module with 'Update-Module PnP.PowerShell' also restores the link."
              }
            }
            Remove-Variable -Name __pnpHelpText, __pnpHelpUri -ErrorAction SilentlyContinue
            """;

        return await sessions.Get(null).ExecuteAsync(script, MetadataTimeout, cancellationToken);
    }

    [McpServerTool(Name = "pnp_run_command", Destructive = true, OpenWorld = true)]
    [Description("Executes one or more PnP PowerShell commands and returns the result. Commands can be chained with semicolons or newlines. The connection established by Connect-PnPOnline persists across calls that use the same sessionId, so you only need to connect once. This tool can be used repeatedly to accomplish complex multi-step tasks.")]
    public static async Task<string> RunPnpCommand(
        PowerShellSessionManager sessions,
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("PnP PowerShell command(s) to execute (e.g., \"Get-PnPSite\", \"Get-PnPList | Select-Object Title, ItemCount\")")] string command,
        [Description("Session to run in. Sessions keep their own PnP connection, so use a second name only when working against two tenants at once (default: \"default\")")] string? sessionId = null,
        [Description("Set to true to approve a destructive command (Remove-*, Clear-*, Reset-*, ...) without an interactive confirmation prompt")] bool confirmDestructive = false,
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
        // CommandTimeout rather than failing early, and a call cannot take twice the configured limit.
        var budget = CommandTimeout;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        // Parsed rather than pattern-matched, so aliases and indirect invocation are seen for what they are.
        var (analysis, sessionError) = await ScriptAnalyzer.AnalyzeAsync(session, command, budget, cancellationToken);

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

        // Read-only mode is skipped entirely: anything that survived Enforce has been parsed and proven
        // not to change Microsoft 365, so a prompt would be noise -- and the textual check's false
        // positives (a destructive name inside a string) must not leak into a mode that already verified
        // the script. Reaching here in read-only mode implies a non-null analysis; a null one failed closed.
        string? flagged = null;
        if (!CommandPolicy.ReadOnlyMode)
        {
            // Both checks run: a needless prompt costs a click, a missed one costs tenant data.
            flagged = analysis is null ? null : CommandPolicy.FindNeedingConfirmation(analysis);

            // Textual fallback catches a destructive name used only as an argument, e.g. Set-Alias nuke Remove-PnPTenantSite.
            if (flagged is null && DestructiveCommandRegex().Match(command) is { Success: true } textual)
            {
                flagged = textual.Value;
            }
        }
        if (flagged is not null && !confirmDestructive && !ConfirmationDisabled)
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

        // Whatever the analysis consumed is deducted, with a floor so the command still gets a chance.
        var remaining = budget - elapsed.Elapsed;
        if (remaining < TimeSpan.FromSeconds(10))
        {
            remaining = TimeSpan.FromSeconds(10);
        }

        return PnPErrorHints.Enrich(await session.ExecuteAsync(script, remaining, cancellationToken));
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
            // The approval is only valid for the command the user was actually shown.
            if (!IsApprovalBoundTo(context.Params.RequestState, command))
            {
                return
                    $"Cancelled: the command changed after it was approved, so nothing was run. " +
                    $"Re-run 'pnp_run_command' and confirm the new command.";
            }

            var elicited = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);

            if (elicited?.IsAccepted is not true)
            {
                return $"Cancelled: {matchedCmdlet} was not confirmed, so nothing was run.";
            }

            // Requires an explicit true. The field carries Default = false, so an accepted response
            // that omits it means the user left the box unticked — treating that as approval would
            // run a destructive command nobody agreed to.
            var confirmed = elicited.Content?.TryGetValue("confirm", out var confirmValue) is true &&
                            confirmValue.ValueKind is JsonValueKind.True;

            return confirmed
                ? null
                : $"Cancelled: {matchedCmdlet} was not explicitly confirmed, so nothing was run. To proceed, call 'pnp_run_command' again with confirmDestructive set to true.";
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

        // Clients that cannot prompt still get a way through, just not a silent one.
        return $"""
            Blocked: this command needs confirmation and has not been confirmed. Nothing was run.

            Flagged: {matchedCmdlet}

            Command:
            {command}

            Show this command to the user and, once they confirm, call 'pnp_run_command' again with confirmDestructive set to true.
            Set the environment variable PNP_MCP_CONFIRM_DESTRUCTIVE=false to turn this check off entirely.
            """;
    }

    [McpServerTool(Name = "pnp_get_connection_status", ReadOnly = true, OpenWorld = false)]
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
            """;
    }

    [McpServerTool(Name = "pnp_reset_session", Destructive = true, Idempotent = true, OpenWorld = false)]
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
    // Keys are matched against the "## " headings in best-practices.md; the values are heading prefixes.
    private static readonly Dictionary<string, string[]> BestPracticeSections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["workflow"] = ["Recommended Workflow", "Prerequisites", "Summary"],
        ["docs"] = ["Finding More About a Cmdlet"],
        ["sessions"] = ["Sessions"],
        ["config"] = ["Server Configuration"],
        ["readonly"] = ["Read-Only Mode"],
        ["destructive"] = ["Destructive Commands"],
        ["auth"] = ["Authentication Best Practices"],
        ["execution"] = ["Execution Best Practices", "Working with Complex Data", "Debugging and Verbose Output"],
        ["patterns"] = ["Common Patterns", "Areas Covered by PnP PowerShell"],
    };

    [McpServerTool(Name = "pnp_get_best_practices", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns best practices for using this MCP server with PnP PowerShell. The full document is long, so pass a section to retrieve only what you need.")]
    public static async Task<string> GetPnpBestPractices(
        [Description("Optional topic to return instead of the whole document. One of: workflow, docs, sessions, config, readonly, destructive, auth, execution, patterns. Omit for everything.")] string? section = null)
    {
        var document = await LoadBestPracticesAsync();

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

    private static async Task<string> LoadBestPracticesAsync()
    {
        // Try to load best-practices.md from the application directory
        var bestPracticesPath = Path.Combine(AppContext.BaseDirectory, "best-practices.md");
        if (File.Exists(bestPracticesPath))
        {
            return await File.ReadAllTextAsync(bestPracticesPath);
        }

        // Try from the working directory (dev scenario)
        bestPracticesPath = Path.Combine(Directory.GetCurrentDirectory(), "best-practices.md");
        if (File.Exists(bestPracticesPath))
        {
            return await File.ReadAllTextAsync(bestPracticesPath);
        }

        // Fallback to inline content
        return GetInlineBestPractices();
    }

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

    private static string GetInlineBestPractices()
    {
        return """
            # Best Practices for Using PnP PowerShell via MCP Server

            ## Recommended Workflow

            Use this flow for reliable execution:
            1. Check connection with `pnp_get_connection_status`.
            2. Search commands with `pnp_search_commands`.
            3. Read syntax and examples with `pnp_get_command_docs`.
            4. Execute with `pnp_run_command` in small, verifiable steps.

            ## Finding More About a Cmdlet

            - Every cmdlet carries a `HelpUri` pointing at its page on https://pnp.github.io/powershell/.
              `pnp_search_commands` returns it per result; `pnp_get_command_docs` ends with it.
            - The local help comes from the installed module, so it can lag the published page and
              sometimes omits examples. When it is not enough, fetch the URL with the client's web-fetch
              tool; if there is no such tool, give the user the link.
            - Never guess a parameter name. If the local help does not list it, fetch the page or ask.
            - Use the returned `HelpUri` as-is; do not hand-assemble a docs URL from the cmdlet name.
            - If a cmdlet reports no HelpUri, it is almost certainly an older PnP.PowerShell build.
              Search https://pnp.github.io/powershell/ for the cmdlet name, or search the web for
              "PnP PowerShell <Cmdlet-Name>", instead of inventing a URL. Suggest
              `Update-Module PnP.PowerShell` (then a server restart) if it keeps happening.

            ## Sessions

            - Commands run in a persistent PowerShell session, so a `Connect-PnPOnline` connection
              stays alive across calls. Connect once, then reuse it.
            - **Omit `sessionId` for normal work**; everything then shares the session `default`.
              Accepted by `pnp_run_command`, `pnp_get_connection_status` and `pnp_reset_session`.
            - Use a second `sessionId` only to hold two tenant or account connections at once, since
              one session holds one connection. Each session has its own variables too.
            - One command runs at a time per session. A second call waits, then reports the session is
              busy; use a different `sessionId` to run two things at once.
            - Use `pnp_reset_session` to sign out, switch accounts, or recover a stuck session.
            - A session is dropped after 30 minutes of inactivity; simply reconnect if that happens.
              A session busy running a command is never dropped, however long it takes.

            ## Server Configuration

            Behaviour is controlled by environment variables the **user** sets in their MCP client
            config, not something this server can change at runtime. If a limit is in the way, explain
            which variable to set and let the user decide:

            - `PNP_MCP_READONLY=true` — refuse anything that would change Microsoft 365.
            - `PNP_MCP_COMMAND_TIMEOUT_SECONDS` — per-command limit, default 600.
            - `PNP_MCP_CONFIRM_DESTRUCTIVE=false` — skip destructive confirmations.
            - `PNP_SCRIPT_SAMPLES_PATH` — local clone of the script samples repo.

            A change takes effect only after the server restarts.

            ## Authentication

            - Always start sessions with `Connect-PnPOnline`.
            - Prefer secure auth methods: `-Interactive`, certificate-based (`-ClientId`, `-Tenant`, `-Thumbprint`), or managed identity.
            - Avoid storing credentials in scripts; use Azure Key Vault or environment variables.
            - Check connection status before running commands to avoid auth errors.

            ## Read-Only Mode

            When `PNP_MCP_READONLY=true`, anything that would change Microsoft 365 is refused.

            - **Allowed**: `Get-`, `Export-`, `Test-`, `Convert-`, `ConvertTo-`, `ConvertFrom-`, `Read-`,
              `Measure-`, `Connect-`, `Disconnect-`, `Find-`, `Format-`, `Resolve-`, `Write-`, `Search-`,
              `Show-`, `Compare-`, and pipeline shaping (`Select-`, `Where-`, `Sort-`, `Group-`,
              `ForEach-`, `Out-`, `Join-`, `Split-`).
            - **Refused**: `Set-`, `Remove-`, `Add-`, `New-`, `Clear-`, `Invoke-`, `Update-`, `Move-`,
              `Enable-`, `Disable-`, `Grant-`, `Revoke-`, `Copy-`, `Import-`, `Restore-`, `Reset-`,
              `Rename-`, `Start-`, `Stop-`, `Register-`, `Unregister-`, and every other change verb.
            - Also refused: commands invoked indirectly (`& $var`), native executables, and
              state-changing method calls (`ExecuteQuery`, `DeleteObject`).
            - Aliases are resolved by parsing, so `rm` is treated as `Remove-Item`.
            - Local file output (`Out-File`, `Export-*`) is still allowed.

            ## Destructive Commands

            - Destructive verbs (`Remove-*`, `Clear-*`, `Reset-*`, `Revoke-*`, `Disable-*`, ...) require
              confirmation before they run. On clients that support prompting you will be asked directly;
              elsewhere the command is blocked until it is re-sent with `confirmDestructive: true`.
            - A command invoked indirectly also requires confirmation, since it cannot be identified.
            - The check prefers asking too often to missing something, so it also matches a destructive
              name that only appears as text. You may be asked about a harmless command occasionally.
            - Always show the user the exact command before asking them to confirm it.

            ## Execution Tips

            - Prefer idempotent reads before writes (`Get-*` before `Set-*`, `Add-*`, `Remove-*`).
            - For complex tasks, run commands incrementally and validate outputs between steps.
            - Return only required properties using `Select-Object` to keep outputs concise.
            - Use explicit site URLs, tenant identifiers, and object IDs to reduce ambiguity.
            - Handle errors with `try/catch` in command chains.
            - Use `-ErrorAction Stop` for predictable error behavior.

            ## Output Tips

            - Use `| Select-Object Property1, Property2` to limit output size.
            - Use `| Where-Object { $_.Property -eq 'Value' }` for filtering.
            - For large result sets, use `-PageSize` parameter where available.
            - Pipe to `ConvertTo-Json` for structured output when needed.

            ## Common Patterns

            ### Connect to a site
            ```powershell
            Connect-PnPOnline -Url https://contoso.sharepoint.com/sites/MySite -Tenant contoso.onmicrosoft.com -ClientId xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx -Thumbprint xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
            ```

            ### List all site collections
            ```powershell
            Get-PnPTenantSite | Select-Object Url, Title, Template
            ```

            ### Get items from a list
            ```powershell
            Get-PnPListItem -List "Documents" -PageSize 100 | Select-Object Id, FieldValues
            ```
            """;
    }

    /// <summary>Identifies the exact command text an approval was granted for, hashed to stay small.</summary>
    internal static string Fingerprint(string command) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command)));

    /// <summary>True only when the echoed request state was minted for exactly this command text.</summary>
    // Fails closed on a missing state: an approval that cannot be tied to what the user saw is not one.
    internal static bool IsApprovalBoundTo(string? requestState, string command) =>
        requestState is not null && string.Equals(requestState, Fingerprint(command), StringComparison.Ordinal);

    private static bool LooksLikePnpCommand(string command)
    {
        return command.Contains("-PnP", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeSingleQuotedPowerShell(string value)
    {
        return value.Replace("'", "''");
    }
}
