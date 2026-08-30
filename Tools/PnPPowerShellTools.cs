using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Services;
using System.ComponentModel;
using System.Reflection;
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

    /// <summary>A sign-in answers in seconds or is waiting for a person, so it gets its own short limit.</summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(2);

    /// <summary>What to say instead of the generic timeout advice, which is meaningless for a sign-in.</summary>
    internal static readonly string SignInTimedOut = $"""
        Error: The sign-in did not finish within {SignInTimeout.TotalMinutes:0.#} minutes and the session was terminated. Nothing is connected.

        A sign-in that runs this long is waiting for a person: a browser prompt or a device code that nobody
        answered. That prompt cannot be seen from this conversation, so waiting longer would not have helped.

        Run 'pnp_diagnose_connection' with the site you are targeting. It reports what this machine can sign in
        with, and if a sign-in has to happen interactively, hand that command to the user to run in their own
        PowerShell 7 terminal with -PersistLogin so later connects need no prompt.
        """;

    // Anchored, and no separator that can run a second command: ; newline | & && ||. A chained connect
    // keeps the full command budget.
    [GeneratedRegex(@"^Connect-PnPOnline\b[^;|&\r\n]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignInOnlyRegex();

    // A backtick-newline continues one statement, so it is folded away before the single-line check.
    [GeneratedRegex(@"`[ \t]*\r?\n[ \t]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineContinuationRegex();

    internal static bool IsSignIn(string command) =>
        SignInOnlyRegex().IsMatch(LineContinuationRegex().Replace(command.Trim(), " "));

    /// <summary>Installing a module changes the machine, so it happens only when the operator opts in.</summary>
    private static bool SetupAllowed =>
        string.Equals(Environment.GetEnvironmentVariable("PNP_MCP_ALLOW_SETUP"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A module install reaches PSGallery and can be slow, so it gets a generous limit of its own.</summary>
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The exact install command, so the message and the run cannot describe different things.</summary>
    internal static string InstallModuleCommand(bool prerelease) =>
        "Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force -AllowClobber" +
        (prerelease ? " -AllowPrerelease" : string.Empty);

    /// <summary>What to return when setup is not allowed: the command the user runs by hand instead.</summary>
    internal static string SetupDisabledMessage(bool prerelease)
    {
        // The command already carries -AllowPrerelease when asked, so only the released path needs the hint.
        var prereleaseHint = prerelease
            ? string.Empty
            : "Add -AllowPrerelease to get the latest pre-release build instead of the released one. ";

        return $"""
            Environment setup is disabled, so nothing was installed and your machine was not changed. This
            server installs software only when you opt in: set PNP_MCP_ALLOW_SETUP=true and call this tool
            again, or run this yourself in a PowerShell 7 terminal:

              {InstallModuleCommand(prerelease)}

            {prereleaseHint}This installs the PnP.PowerShell module only; it does not sign you in. After it
            finishes, call 'pnp_diagnose_connection' with your site to see how to connect.
            """;
    }

    [McpServerTool(Name = "pnp_search_commands", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Answers the question \"which cmdlet handles this, and what is it called?\". Searches cmdlet names, verbs and nouns by keyword to discover whether one exists for the area you need to manage. Use it whenever the cmdlet name is unknown. Returns names and documentation links, never tenant data.")]
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

        var result = await sessions.Get(null).ExecuteAsync(script, MetadataTimeout, cancellationToken, $"search-commands\n{query}\n{limit}");

        // Searching cmdlet names needs no tenant, so an unusable environment falls back to the vendored index.
        if (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            return OutputLimit.Apply(
                SearchVendoredCommands(query!, limit, result),
                "Search with fewer keywords, or pass a smaller 'limit' to return fewer results.",
                PnPErrorHints.HintFor(result));
        }

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

    /// <summary>Answers a command search from the vendored index when the live one could not run.</summary>
    private static string SearchVendoredCommands(string query, int limit, string sessionError)
    {
        var terms = query.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = CommandIndex.Search(terms, limit);

        // Results first, error last: truncation should cost the error, not the cmdlets.
        var sb = new StringBuilder();
        sb.AppendLine("Answered from the vendored cmdlet index: the live lookup failed, and its error is at the end.");
        sb.AppendLine("Nothing below was checked against your installed module, and no command can run until that is fixed.");
        sb.AppendLine();

        if (matches.Count == 0)
        {
            sb.AppendLine($"No vendored cmdlet name matched '{OutputLimit.Echo(query)}'.");
        }
        else
        {
            foreach (var name in matches)
            {
                sb.AppendLine($"- {name} — {CommandIndex.DocsUrl(name)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(CommandIndex.Provenance);
        sb.AppendLine();
        sb.AppendLine(sessionError);

        return sb.ToString();
    }

    [McpServerTool(Name = "pnp_get_command_docs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets the reference documentation for one named cmdlet: its syntax, every parameter and what it means, the parameter sets, and worked examples. Use it once you know the cmdlet name and need to know how to call it.")]
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

        // Markdown first, and before the help: a trailing link is what the cap drops.
        var links = CommandIndex.MarkdownUrl(commandName) is { } markdown
            ? $"""
              MARKDOWN DOCUMENTATION (prefer this — the same page in source form, at a fraction of the tokens): {markdown}
              HTML DOCUMENTATION: {CommandIndex.DocsUrl(commandName)}
              TIP: If the syntax or examples below look incomplete, fetch the markdown -- it is generated from the current source and usually carries more parameter detail and examples than the shipped help. If you cannot fetch pages, give the user the link instead.

              """
            : string.Empty;

        // The session's own HelpUri is consulted only for a cmdlet newer than this build.
        var script = $$"""
            $__pnpName = '{{safeCommandName}}'
            $__pnpHelpText = Get-Help $__pnpName -Full | Out-String
            if ([string]::IsNullOrWhiteSpace($__pnpHelpText)) {
              Write-Output "No documentation found for '$__pnpName'. Verify the command name using 'pnp_search_commands'."
            } else {
              if ({{(links.Length > 0 ? "$false" : "$true")}}) {
                $__pnpHelpUri = $null
                try { $__pnpHelpUri = ($ExecutionContext.InvokeCommand.GetCommand($__pnpName, [System.Management.Automation.CommandTypes]::All)).HelpUri } catch { $__pnpHelpUri = $null }
                if (-not [string]::IsNullOrWhiteSpace($__pnpHelpUri)) {
                  Write-Output "ONLINE DOCUMENTATION: $__pnpHelpUri"
                  Write-Output "NOTE: This cmdlet is not in this server's vendored index, so it is newer than this build. The link above comes from your installed module."
                } else {
                  Write-Output "NOTE: This cmdlet reports no documentation URL, which usually means an older PnP.PowerShell build (HelpUri is populated in current versions)."
                  Write-Output "FALLBACK: Search https://pnp.github.io/powershell/ for '$__pnpName' to find its page, or search the web for 'PnP PowerShell $__pnpName'. Do not hand-assemble a docs URL -- the path pattern is not guaranteed. Updating the module with 'Update-Module PnP.PowerShell' also restores the link."
                }
                Write-Output ''
              }
              Write-Output $__pnpHelpText
            }
            Remove-Variable -Name __pnpName, __pnpHelpText, __pnpHelpUri -ErrorAction SilentlyContinue
            """;

        var help = await sessions.Get(null).ExecuteAsync(script, MetadataTimeout, cancellationToken, $"command-docs\n{commandName.Trim()}");

        // Capped: a session error carries unbounded prior output.
        if (help.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) && links.Length > 0)
        {
            return OutputLimit.Apply(
                help + "\n\nLocal help is unavailable, but the published documentation is not:\n",
                "Read the documentation pages linked below for the reference this session could not produce.",
                "\n" + links + PnPErrorHints.HintFor(help));
        }

        return OutputLimit.Apply(links + help, "Read the documentation pages linked above for the full reference.", PnPErrorHints.HintFor(help));
    }

    [McpServerTool(Name = "pnp_run_command", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Runs PnP PowerShell against the connected tenant and returns what it produced. This is the tool that does the actual work: use it to create, read, update, set, change, add, remove, delete, list, upload, download, copy, move, restore or report on sites, lists, libraries, files, folders, list items, users, groups, permissions, Teams, Entra ID objects, and tenant or site settings. Chain steps with semicolons or newlines. The Connect-PnPOnline connection persists across calls sharing a sessionId, so connect once and keep going.")]
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

                Installing, updating or registering is not one of those: Install-Module, Update-Module and
                Register-PnPEntraIDApp* change the user's machine or tenant rather than run against a connection,
                so this server does not run them. Show the command to the user to run in their own PowerShell 7
                terminal instead of looking for a cmdlet here.
                """;
        }

        var session = sessions.Get(sessionId);

        // Analysis and execution share one budget, so queuing behind a long command still waits out
        // CommandTimeout rather than failing early, and a call cannot exceed the configured limit.
        var signIn = IsSignIn(command);
        var budget = signIn ? SignInTimeout : CommandTimeout;
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

        var (result, held) = await session.ExecuteAndCaptureAsync(script, remaining, cancellationToken, $"run\n{command}");

        // The generic timeout advice is meaningless for a sign-in.
        if (signIn && result.Contains(PowerShellSession.TerminatedMarker, StringComparison.Ordinal))
        {
            return OutputLimit.Apply(SignInTimedOut);
        }

        // Summarised and paged rather than cut mid-token, so the answer stays complete and parseable.
        if (held is not null)
        {
            return OutputLimit.Apply(ResultSummary.Render(held, 0, session.Id));
        }

        // The hint is reserved as a suffix rather than appended after capping, so the response stays
        // inside PNP_MCP_MAX_OUTPUT_CHARS and the "Likely cause" line still survives a truncation.
        return OutputLimit.Apply(result, suffix: PnPErrorHints.HintFor(result));
    }

    [McpServerTool(Name = "pnp_get_result_page", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the next page of a result set that pnp_run_command summarised because it was too large to return whole. Pages over rows already fetched, so it costs nothing against the tenant and returns exactly the rows the original command saw. Use the cursor and offset printed under the summary.")]
    public static string GetPnpResultPage(
        PowerShellSessionManager sessions,
        [Description("The cursor printed with the summary, e.g. \"a1b2c3d4e5\"")] string cursor,
        [Description("Zero-based row number to start from, as printed in the MORE line of the previous page")] int offset = 0)
    {
        var session = sessions.FindHolder(cursor);

        // Read once: a concurrent command in that session clears Held.
        if (session?.Held is not { } held)
        {
            return
                $"Error: No held result set matches cursor '{OutputLimit.Echo(cursor)}'. A cursor is dropped when the next command runs in " +
                "its session, when the session is reset, and when the server restarts. Re-run the original command to get a new one.";
        }

        return OutputLimit.Apply(ResultSummary.Render(held, offset, session.Id));
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
    [Description("Diagnoses a broken or unfamiliar machine and reports whether its environment is set up, and answers how to sign in to it. Verifies everything that must be true before anything can run: pwsh installed and on PATH, the PnP.PowerShell module present and current enough, whether this session is connected, and which app registration, persisted login or certificate this machine can actually authenticate with. Every failing check names its cause and the next command to run, filling in every value the machine's own state can supply. Call it before the first Connect-PnPOnline rather than composing one from memory, and whenever anything fails for a reason that is not obvious.")]
    public static async Task<string> DiagnosePnpConnection(
        PowerShellSessionManager sessions,
        [Description("Session to inspect (default: \"default\")")] string? sessionId = null,
        [Description("The SharePoint site the caller wants to work against, e.g. \"https://contoso.sharepoint.com/sites/marketing\". Given one, the report names the exact connect command for that host instead of a template.")] string? targetUrl = null,
        CancellationToken cancellationToken = default)
    {
        var facts = await ConnectionPreflight.GatherAsync(sessions, sessionId, targetUrl, cancellationToken);

        return OutputLimit.Apply(
            ConnectionPreflight.Render(facts),
            "Raise PNP_MCP_MAX_OUTPUT_CHARS to see the whole report; this one is a fixed set of checks, so there is nothing to narrow.");
    }

    [McpServerTool(Name = "pnp_get_connection_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reports the current state of one session: whether it is signed in right now, which site URL it holds, and which account it is authenticated as. Use it to find out who you are and where you are pointed before doing anything else.")]
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

        var result = await sessions.Get(sessionId).ExecuteAsync(script, MetadataTimeout, cancellationToken, "connection-status");

        return $"""
            Session: {OutputLimit.Echo(string.IsNullOrWhiteSpace(sessionId) ? PowerShellSessionManager.DefaultSessionId : sessionId.Trim())}

            {result}
            """ + PnPErrorHints.HintFor(result);
    }

    [McpServerTool(Name = "pnp_reset_session", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Signs out. Ends a session and discards everything held in it, so the next call starts fresh and must reconnect. Use it to log out, to switch to a different account, or to recover a session that has wedged or stopped responding.")]
    public static async Task<string> ResetPnpSession(
        PowerShellSessionManager sessions,
        [Description("Session to end (default: \"default\")")] string? sessionId = null)
    {
        var name = OutputLimit.Echo(string.IsNullOrWhiteSpace(sessionId) ? PowerShellSessionManager.DefaultSessionId : sessionId.Trim());
        var existed = await sessions.ResetAsync(sessionId);

        var active = sessions.Describe();
        var summary = active.Count == 0
            ? "No sessions are currently running."
            : "Sessions: " + string.Join(", ", active.Select(s => $"{OutputLimit.Echo(s.Id)} ({(!s.IsAlive ? "stopped" : s.IsBusy ? "running" : "idle")})"));

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
    [Description("Returns this server's own guidance and recommended workflow: how to approach a task, the rules it enforces, and the conventions to follow. The full document is long, so pass a section to read one topic.")]
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
                $"Error: Unknown section '{OutputLimit.Echo(key)}'. Valid sections are: {string.Join(", ", BestPracticeSections.Keys)}. " +
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

    private static DateTimeOffset ServerStartedUtc =>
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    [McpServerTool(Name = "pnp_setup_environment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Installs the PnP.PowerShell module for the current user so PnP cmdlets can run, choosing the released build or the latest pre-release. It installs only that module: it never signs in, never touches the tenant, and creates no app registration. For safety it installs only when PNP_MCP_ALLOW_SETUP=true; otherwise it returns the exact Install-Module command to run by hand. Use it when the PnP.PowerShell module is not installed.")]
    public static async Task<string> SetupEnvironment(
        PowerShellSessionManager sessions,
        [Description("Install the latest pre-release build instead of the released one (adds -AllowPrerelease). Ask the user which they want. Default: false.")] bool prerelease = false,
        [Description("Session to run the install in (default: \"default\")")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (!SetupAllowed)
        {
            return OutputLimit.Apply(SetupDisabledMessage(prerelease));
        }

        // pwsh has to exist before a module can be installed into it.
        var environment = await ConnectionPreflight.ProbeEnvironmentAsync(cancellationToken);

        if (environment.PwshVersion is null)
        {
            return OutputLimit.Apply($"""
                Error: PowerShell 7 (pwsh) is not available, so the PnP.PowerShell module cannot be installed. {environment.ProbeError}
                Install PowerShell 7 first from https://aka.ms/powershell, then call this tool again.
                """);
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
              {{InstallModuleCommand(prerelease)}}
              $__pnpMod = Get-Module -ListAvailable -Name PnP.PowerShell | Sort-Object Version -Descending | Select-Object -First 1
              if ($__pnpMod) { "Installed PnP.PowerShell $($__pnpMod.Version). Next: call pnp_diagnose_connection with your site to see how to connect." }
              else { "Error: the install command reported success but PnP.PowerShell is still not visible. Check the PowerShell 7 module path." }
            } catch {
              "Error: $($_.Exception.Message)"
            }
            Remove-Variable -Name __pnpMod -ErrorAction SilentlyContinue
            """;

        var result = await sessions.Get(sessionId).ExecuteAsync(script, SetupTimeout, cancellationToken, "setup-environment");

        return OutputLimit.Apply(result);
    }

    [McpServerTool(Name = "pnp_ping", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the server version, uptime, read-only mode status, and active session count, and (unless includeReadiness is false) whether pwsh and the PnP.PowerShell module are present on this machine. Use this as a lightweight health check to confirm the server is responsive and ready to run PnP commands.")]
    public static async Task<string> Ping(
        PowerShellSessionManager sessions,
        [Description("Also probe whether pwsh and the PnP.PowerShell module are installed (launches a short-lived pwsh). Default: true.")] bool includeReadiness = true,
        CancellationToken cancellationToken = default)
    {
        var version = typeof(PnPPowerShellTools).Assembly.GetName().Version;
        var packageVersion = typeof(PnPPowerShellTools).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? version?.ToString(3) ?? "0.0.0";
        var uptime = DateTimeOffset.UtcNow - ServerStartedUtc;
        var active = sessions.Describe();
        var readOnly = CommandPolicy.ReadOnlyMode;

        var readiness = string.Empty;

        if (includeReadiness)
        {
            var environment = await ConnectionPreflight.ProbeEnvironmentAsync(cancellationToken);
            var moduleVersion = environment.ModuleVersion is null ? "null" : $"\"{environment.ModuleVersion}\"";

            readiness = $$"""
                ,
                  "pwshAvailable": {{(environment.PwshVersion is not null ? "true" : "false")}},
                  "pnpModuleInstalled": {{(environment.ModuleVersion is not null ? "true" : "false")}},
                  "pnpModuleVersion": {{moduleVersion}}
                """;
        }

        return $$"""
            {
              "status": "ok",
              "version": "{{version?.ToString(3) ?? "0.0.0"}}",
              "packageVersion": "{{packageVersion}}",
              "uptime": "{{uptime:d\d\ hh\:mm\:ss}}",
              "startedUtc": "{{ServerStartedUtc:O}}",
              "readOnlyMode": {{(readOnly ? "true" : "false")}},
              "activeSessions": {{active.Count}}{{readiness}}
            }
            """;
    }

    [McpServerTool(Name = "pnp_list_sessions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists all active PowerShell sessions with their status and last activity time. Use this to see what sessions exist before deciding which to connect, reset, or reuse.")]
    public static string ListSessions(PowerShellSessionManager sessions)
    {
        var active = sessions.Describe();

        if (active.Count == 0)
        {
            return "No sessions are currently running. A session is created automatically when you first call a tool that requires one.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"**{active.Count}** active session(s):\n");
        sb.AppendLine("| Session | Status | Last Activity (UTC) |");
        sb.AppendLine("|---------|--------|---------------------|");

        foreach (var (id, isAlive, isBusy, lastUsed) in active)
        {
            var status = !isAlive ? "stopped" : isBusy ? "running" : "idle";
            var safeId = id.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
            sb.AppendLine($"| {safeId} | {status} | {lastUsed:yyyy-MM-dd HH:mm:ss} |");
        }

        sb.AppendLine();
        sb.AppendLine("TIP: Use `pnp_get_connection_status` with a sessionId to check what a session is connected to.");
        sb.AppendLine("TIP: Use `pnp_reset_session` to end a session that is no longer needed.");

        return OutputLimit.Apply(sb.ToString());
    }
}
