using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Models;
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

    [McpServerTool(
        Name = "pnp_search_commands",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandSearchResult))]
    [Description("Answers the question \"which cmdlet handles this, and what is it called?\". Searches cmdlet names, verbs, nouns, descriptions, parameters and examples to discover whether one exists for the area you need to manage. Describe the task in your own words -- \"add a column to a list\", \"share a file externally\" -- or pass keywords. Use it whenever the cmdlet name is unknown. Returns names, synopses, parameters and documentation links, never tenant data.")]
    public static CallToolResult SearchPnpCommands(
        [Description("What you are trying to do, in plain words or as keywords (e.g., \"find sites with no owner\", \"teams channel\", \"upload file\")")] string query,
        [Description("Maximum number of results to return (default: 20, max: 100)")] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);

        if (string.IsNullOrWhiteSpace(query))
        {
            return StructuredResult.Text("Error: Please provide at least one search keyword.", isError: true);
        }

        // Answered from the compiled-in corpus: no pwsh round-trip, so this works before the module is
        // installed and costs no session time. Search resolves a superseded alias to its current cmdlet,
        // so an alias query needs no special case here.
        var found = CommandCorpus.Search(query, limit);
        var aliased = CommandCorpus.AliasTarget(query.Trim());

        // Lean drops per-cmdlet detail, for the case where one cmdlet alone overruns a low cap --
        // Set-PnPTenant carries some two hundred parameter names.
        return StructuredResult.FitToCap(
            found,
            (hits, lean) => Describe(query, found.Count, hits, aliased, lean),
            ToolOutputJsonContext.Default.CommandSearchResult,
            RenderSearch);
    }

    /// <param name="found">How many the search returned, so Truncated needs no separate flag.</param>
    /// <param name="lean">Omits parameters, examples and permissions, for when one hit alone exceeds the cap.</param>
    private static CommandSearchResult Describe(
        string query,
        int found,
        IReadOnlyList<IndexedCommand> hits,
        string? aliased,
        bool lean) =>
        new()
        {
            // Clamped, like the text half already does: the query is caller-supplied and unbounded, and
            // echoing it whole let a 100k-character query dominate a payload that shrinking hits could
            // never bring back under the cap.
            Query = OutputLimit.Echo(query.Trim()),
            Matched = found,
            DetailOmitted = lean,
            AliasResolvedTo = aliased,
            IndexedModuleVersion = CommandCorpus.ModuleVersion,
            Commands = [.. hits.Select(c => new CommandSearchHit
            {
                Name = c.Name,
                Verb = c.Verb,
                Noun = c.Noun,
                Synopsis = c.Synopsis,
                Parameters = lean ? null : [.. c.Parameters.Select(p => p.Name)],
                Examples = !lean && c.Examples is { Count: > 0 } ? c.Examples : null,
                RequiredPermissions = !lean && c.Permissions is { Count: > 0 } ? c.Permissions : null,
                DocsUrl = CommandCorpus.DocsUrl(c),
            })],
        };

    /// <summary>Renders a search answer from a constructed result, so the honesty checks can build wrong ones.</summary>
    internal static string RenderForTest(CommandSearchResult result) => RenderSearch(result);

    /// <summary>The human-readable half of a search answer; clients that ignore schemas still get everything.</summary>
    private static string RenderSearch(CommandSearchResult result)
    {
        var sb = new StringBuilder();

        if (result.Count == 0)
        {
            sb.AppendLine($"No cmdlet matched '{OutputLimit.Echo(result.Query)}'.");
            sb.AppendLine();

            // A query the tokenizer discarded entirely looks identical to a genuine miss, which reads as
            // "no such cmdlet exists" and stops the search there.
            if (Bm25Tokenizer.Tokenize(result.Query).Count == 0)
            {
                sb.AppendLine("Nothing in that query could be indexed. The cmdlet index is English and matches");
                sb.AppendLine("letters and digits only, so accents aside, other scripts do not match. Try English");
                sb.AppendLine("keywords -- \"create site\", \"list item\", \"upload file\".");
            }
            else
            {
                sb.AppendLine("Try fewer or more general words (\"site\" rather than \"site collection owner report\"), or");
                sb.AppendLine("search the published index at https://pnp.github.io/powershell/cmdlets/index.html.");
            }

            return OutputLimit.Apply(sb.ToString(), suffix: "\n" + CommandCorpus.Provenance);
        }

        // The number that matched, not the number that fitted: stating the page as the count is the same
        // false-rather-than-partial claim that "N active sessions" made.
        sb.AppendLine(result.Truncated
            ? $"{result.Matched} cmdlet(s) for '{OutputLimit.Echo(result.Query)}', showing the first {result.Count} — the rest did not fit the output cap:"
            : $"{result.Count} cmdlet(s) for '{OutputLimit.Echo(result.Query)}', most relevant first:");

        if (result.AliasResolvedTo is { } current)
        {
            sb.AppendLine($"NOTE: you searched a superseded alias; {current} is the current name.");
        }

        if (result.Truncated)
        {
            sb.AppendLine("Narrow the query, or pass a smaller 'limit', to see the rest.");
        }

        if (result.DetailOmitted)
        {
            sb.AppendLine("Parameters and examples are omitted below: one cmdlet alone exceeded the output cap.");
        }

        sb.AppendLine();

        foreach (var hit in result.Commands)
        {
            sb.AppendLine($"- {hit.Name} — {hit.Synopsis}");

            if (hit.Parameters is { Count: > 0 })
            {
                sb.AppendLine($"  Parameters: {string.Join(", ", hit.Parameters)}");
            }

            if (hit.Examples is { Count: > 0 } examples)
            {
                sb.AppendLine($"  Example: {examples[0]}");
            }

            if (hit.DocsUrl is not null)
            {
                sb.AppendLine($"  Docs: {hit.DocsUrl}");
            }
        }

        // Tips last in the body, provenance alone in the suffix -- the same split ScriptSampleTools uses.
        // OutputLimit clamps a suffix to a quarter of the budget and keeps it, so anything sharing that
        // suffix with the provenance line would push it out of a truncated answer. Losing the tips to
        // truncation is fine; losing which module version answered is not.
        sb.AppendLine();
        sb.AppendLine("TIP: Run 'pnp_get_command_docs' for the full syntax and parameter meanings before executing any of these -- the parameters listed above are names only.");
        sb.AppendLine("TIP: The synopses and parameters above come from the module this server was built against. 'pnp_get_command_docs' reads the module you actually have.");
        sb.Append("TIP: For complex tasks, break them into smaller steps and run commands incrementally using 'pnp_run_command'.");

        return OutputLimit.Apply(
            sb.ToString(),
            "Search with fewer keywords, or pass a smaller 'limit' to return fewer results.",
            "\n\n" + CommandCorpus.Provenance);
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

    [McpServerTool(
        Name = "pnp_get_result_page",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ResultPage))]
    [Description("Returns the next page of a result set that pnp_run_command summarised because it was too large to return whole. Pages over rows already fetched, so it costs nothing against the tenant and returns exactly the rows the original command saw. Use the cursor and offset printed under the summary.")]
    public static CallToolResult GetPnpResultPage(
        PowerShellSessionManager sessions,
        [Description("The cursor printed with the summary, e.g. \"a1b2c3d4e5\"")] string cursor,
        [Description("Zero-based row number to start from, as printed in the MORE line of the previous page")] int offset = 0)
    {
        var session = sessions.FindHolder(cursor);

        // Read once: a concurrent command in that session clears Held.
        if (session?.Held is not { } held)
        {
            return StructuredResult.Text(
                $"Error: No held result set matches cursor '{OutputLimit.Echo(cursor)}'. A cursor is dropped when the next command runs in " +
                "its session, when the session is reset, and when the server restarts. Re-run the original command to get a new one.",
                isError: true);
        }

        var (start, end, pageable, _) = ResultSummary.Paging(held, offset);

        var page = new ResultPage
        {
            Cursor = held.Cursor,
            SessionId = session.Id,
            Offset = start,
            TotalRows = held.TotalRows,
            PageableRows = pageable,
            NextOffset = end < pageable ? end : null,
        };

        // Render already sizes the page against the cap, and the structured half carries only the offsets,
        // so there is nothing here to shrink.
        return StructuredResult.From(
            page,
            ToolOutputJsonContext.Default.ResultPage,
            _ => OutputLimit.Apply(ResultSummary.Render(held, offset, session.Id)));
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
    [Description("Diagnoses a broken or unfamiliar machine, and answers how to sign in to it. Verifies everything that must be true before anything can run: pwsh installed and on PATH, the PnP.PowerShell module present and current enough, whether this session is connected, and which app registration, persisted login or certificate this machine can actually authenticate with. Every failing check names its cause and the next command to run, filling in every value the machine's own state can supply. Call it before the first Connect-PnPOnline rather than composing one from memory, and whenever anything fails for a reason that is not obvious.")]
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

    [McpServerTool(
        Name = "pnp_get_connection_status",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ConnectionStatus))]
    [Description("Reports the current state of one session: whether it is signed in right now, which site URL it holds, and which account it is authenticated as. Use it to find out who you are and where you are pointed before doing anything else.")]
    public static async Task<CallToolResult> GetPnpConnectionStatus(
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
        var name = OutputLimit.Echo(string.IsNullOrWhiteSpace(sessionId) ? PowerShellSessionManager.DefaultSessionId : sessionId.Trim());

        // Capped like every other tool that echoes session output: a session-level failure carries
        // whatever the session printed before it died, which is unbounded. This path was never capped,
        // and the oversized-argument test missed it because it varies the session id, not the output.
        var text = OutputLimit.Apply(
            $"""
            Session: {name}

            {result}
            """,
            suffix: PnPErrorHints.HintFor(result));

        // A session-level failure returns prose, not the JSON the script emits, so there is nothing to
        // type. Reporting connected:false there would be a claim about the tenant we cannot support.
        if (ParseStatus(result) is not { } status)
        {
            return StructuredResult.Text(text);
        }

        // Clamped before serializing: these come back from the tenant, and a pathological value would
        // otherwise put the structured half past the cap on its own. 512 leaves real SharePoint URLs
        // intact, which OutputLimit.Echo's 120 would not.
        return StructuredResult.From(
            status with
            {
                SessionId = name,
                Url = OutputLimit.Clamp(status.Url),
                TenantAdminUrl = OutputLimit.Clamp(status.TenantAdminUrl),
                Account = OutputLimit.Clamp(status.Account),
                Message = OutputLimit.Clamp(status.Message),
            },
            ToolOutputJsonContext.Default.ConnectionStatus,
            _ => text);
    }

    /// <summary>Reads the status JSON the session emitted, or null when it did not emit any.</summary>
    // Deliberately strict about what counts as an answer. Deserializing any JSON found in the output
    // yields an all-defaults record, i.e. a confident "connected: false" -- so an error message that
    // merely happens to contain braces would be reported as a fact about the tenant. Two guards: a
    // session-level failure is never parsed, and the payload must carry the property the script always
    // writes rather than merely being valid JSON.
    private static ConnectionStatus? ParseStatus(string output)
    {
        if (output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        var candidate = output[start..(end + 1)];

        try
        {
            using var document = JsonDocument.Parse(candidate);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("connected", out var connected) ||
                connected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            return JsonSerializer.Deserialize(candidate, ToolOutputJsonContext.Default.ConnectionStatus);
        }
        catch (JsonException)
        {
            return null;
        }
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

    [McpServerTool(
        Name = "pnp_ping",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ServerHealth))]
    [Description("Returns the server version, uptime, read-only mode status, and active session count. Use this as a lightweight health check to confirm the server is responsive.")]
    public static CallToolResult Ping(PowerShellSessionManager sessions)
    {
        var version = typeof(PnPPowerShellTools).Assembly.GetName().Version;
        var uptime = DateTimeOffset.UtcNow - ServerStartedUtc;

        var health = new ServerHealth
        {
            Status = "ok",
            Version = version?.ToString(3) ?? "0.0.0",
            PackageVersion = typeof(PnPPowerShellTools).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? version?.ToString(3) ?? "0.0.0",
            Uptime = $"{uptime:d\\d\\ hh\\:mm\\:ss}",
            StartedUtc = ServerStartedUtc,
            ReadOnlyMode = CommandPolicy.ReadOnlyMode,
            ActiveSessions = sessions.Describe().Count,
        };

        // Fixed size whatever the server has been doing, so it needs no shrink-to-fit.
        return StructuredResult.From(health, ToolOutputJsonContext.Default.ServerHealth, RenderHealth);
    }

    private static string RenderHealth(ServerHealth health) =>
        $"""
        Server: ok, version {health.Version} (package {health.PackageVersion})
        Uptime: {health.Uptime}, started {health.StartedUtc:u}
        Read-only mode: {(health.ReadOnlyMode ? "on -- state-changing cmdlets are blocked" : "off")}
        Active sessions: {health.ActiveSessions}
        """;

    [McpServerTool(
        Name = "pnp_list_sessions",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(SessionListResult))]
    [Description("Lists all active PowerShell sessions with their status and last activity time. Use this to see what sessions exist before deciding which to connect, reset, or reuse.")]
    public static CallToolResult ListSessions(PowerShellSessionManager sessions)
    {
        var active = sessions.Describe();

        // A session id is caller-supplied, so it is clamped here as well as escaped when rendered.
        return StructuredResult.FitToCap(
            active,
            (page, _) => new SessionListResult
            {
                Total = active.Count,
                Sessions = [.. page.Select(s => new SessionSummary
                {
                    Id = OutputLimit.Echo(s.Id),
                    Status = !s.IsAlive ? "stopped" : s.IsBusy ? "running" : "idle",
                    LastUsedUtc = s.LastUsedUtc,
                })],
            },
            ToolOutputJsonContext.Default.SessionListResult,
            RenderSessions);
    }

    private static string RenderSessions(SessionListResult result)
    {
        if (result.Total == 0)
        {
            return "No sessions are currently running. A session is created automatically when you first call a tool that requires one.";
        }

        var sb = new StringBuilder();

        // The total, not the page: stating how many fitted as though it were how many exist would be
        // wrong rather than merely partial.
        sb.AppendLine(result.Truncated
            ? $"**{result.Total}** active session(s), showing the first {result.Count} — the rest did not fit the output cap:\n"
            : $"**{result.Count}** active session(s):\n");

        sb.AppendLine("| Session | Status | Last Activity (UTC) |");
        sb.AppendLine("|---------|--------|---------------------|");

        foreach (var session in result.Sessions)
        {
            // Escaped for the table: a pipe or newline in an id would otherwise break the row apart.
            var safeId = session.Id.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
            sb.AppendLine($"| {safeId} | {session.Status} | {session.LastUsedUtc:yyyy-MM-dd HH:mm:ss} |");
        }

        sb.AppendLine();
        sb.AppendLine("TIP: Use `pnp_get_connection_status` with a sessionId to check what a session is connected to.");
        sb.Append("TIP: Use `pnp_reset_session` to end a session that is no longer needed.");

        return OutputLimit.Apply(sb.ToString());
    }
}
