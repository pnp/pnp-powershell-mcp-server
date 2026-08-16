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

    /// <summary>
    /// Verbs that destroy, overwrite, or revoke. These require explicit confirmation before running;
    /// ordinary mutating verbs (Set, Add, New, Enable, Grant, ...) do not, or the prompt would fire
    /// so often it would be clicked through without being read.
    /// </summary>
    /// <remarks>
    /// Deliberately not limited to <c>-PnP</c> cmdlets. Reaching this tool only requires <c>-PnP</c>
    /// somewhere in the script, so a destructive non-PnP command riding along in the same string
    /// (<c>Remove-Item -Recurse -Force ...; Get-PnPWeb</c>) has to be caught too. This is still a
    /// textual check and can be evaded; parsing the AST is the real fix.
    /// </remarks>
    [GeneratedRegex(@"\b(Remove|Clear|Reset|Uninstall|Revoke|Deny|Restore|Move|Rename|Disable)-[A-Za-z]\w*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DestructiveCommandRegex();

    /// <summary>
    /// Per-command wall-clock limit. Generous by default because long tenant-wide operations are the
    /// normal case here; clients that support the Tasks extension avoid the wait entirely by running
    /// the call as a task.
    /// </summary>
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
                  [PSCustomObject]@{ Name = $__pnpCmd.Name; Verb = $__pnpCmd.Verb; Noun = $__pnpCmd.Noun; Score = $__pnpScore }
                }
              } |
              Sort-Object -Property Score -Descending |
              Select-Object -First {{limit}} Name, Verb, Noun |
              ConvertTo-Json -Depth 5 -Compress
            Remove-Variable -Name __pnpTerms, __pnpCmd, __pnpScore, __pnpTerm -ErrorAction SilentlyContinue
            """;

        var result = await sessions.Get(null).ExecuteAsync(script, MetadataTimeout, cancellationToken);

        return $"""
            {result}

            TIP: Before executing any of the commands, run the 'pnp_get_command_docs' tool to retrieve the full syntax, parameters, and examples.
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
            }
            Remove-Variable -Name __pnpHelpText -ErrorAction SilentlyContinue
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

        var destructiveMatch = DestructiveCommandRegex().Match(command);
        if (destructiveMatch.Success && !confirmDestructive && !ConfirmationDisabled)
        {
            var refusal = await ConfirmDestructiveAsync(server, context, command, destructiveMatch.Value);
            if (refusal is not null)
            {
                return refusal;
            }
        }

        // Base64-encode the command so quoting inside it cannot break the wrapper.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        // Wrapper variables are __pnp-prefixed and removed afterwards. The session is shared and
        // long-lived now, so a plain name like $result would silently overwrite the caller's own
        // variable between calls — which is exactly the assign-then-shape pattern best-practices.md
        // tells them to use.
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

        return await sessions.Get(sessionId).ExecuteAsync(script, CommandTimeout, cancellationToken);
    }

    /// <summary>
    /// Returns <see langword="null"/> when the command is approved, or the message to return to the
    /// caller when it is not.
    /// </summary>
    private static async Task<string?> ConfirmDestructiveAsync(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        string command,
        string matchedCmdlet)
    {
        // A retry carries the answer to the prompt raised by the previous attempt.
        if (context.Params?.InputResponses?.TryGetValue("confirmDestructive", out var response) is true)
        {
            // The approval is only valid for the command the user was actually shown. Without this
            // check the retry could carry different arguments and still arrive pre-approved.
            if (!string.Equals(context.Params.RequestState, Fingerprint(command), StringComparison.Ordinal))
            {
                return
                    $"Cancelled: the command changed after it was approved, so nothing was run. " +
                    $"Re-run 'pnp_run_command' and confirm the new command.";
            }

            var elicited = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);

            if (elicited?.IsAccepted is not true)
            {
                return $"Cancelled: '{matchedCmdlet}' was not confirmed, so nothing was run.";
            }

            // Requires an explicit true. The field carries Default = false, so an accepted response
            // that omits it means the user left the box unticked — treating that as approval would
            // run a destructive command nobody agreed to.
            var confirmed = elicited.Content?.TryGetValue("confirm", out var confirmValue) is true &&
                            confirmValue.ValueKind is JsonValueKind.True;

            return confirmed
                ? null
                : $"Cancelled: '{matchedCmdlet}' was not explicitly confirmed, so nothing was run. To proceed, call 'pnp_run_command' again with confirmDestructive set to true.";
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
                            $"This will run a destructive PnP PowerShell command ({matchedCmdlet}) against the connected tenant:\n\n{command}\n\nThis cannot be undone. Continue?",
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
            Blocked: '{matchedCmdlet}' is a destructive command and has not been confirmed. Nothing was run.

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

    [McpServerTool(Name = "pnp_get_best_practices", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns recommended best practices and guidance for using this MCP server with PnP PowerShell commands, including authentication, session handling, error handling, and execution tips.")]
    public static async Task<string> GetPnpBestPractices()
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

            ## Sessions

            - Commands run in a persistent PowerShell session, so a `Connect-PnPOnline` connection
              stays alive across calls. Connect once, then reuse it.
            - Pass the same `sessionId` to keep working in one session. Use a second `sessionId` only
              when you need connections to two tenants at the same time.
            - Use `pnp_reset_session` to sign out, switch accounts, or recover a stuck session.
            - A session is dropped after 30 minutes of inactivity; simply reconnect if that happens.

            ## Authentication

            - Always start sessions with `Connect-PnPOnline`.
            - Prefer secure auth methods: `-Interactive`, certificate-based (`-ClientId`, `-Tenant`, `-Thumbprint`), or managed identity.
            - Avoid storing credentials in scripts; use Azure Key Vault or environment variables.
            - Check connection status before running commands to avoid auth errors.

            ## Destructive Commands

            - Destructive verbs (`Remove-*`, `Clear-*`, `Reset-*`, `Revoke-*`, `Disable-*`, ...) require
              confirmation before they run. On clients that support prompting you will be asked directly;
              elsewhere the command is blocked until it is re-sent with `confirmDestructive: true`.
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

    /// <summary>
    /// Identifies the exact command text an approval was granted for. A hash rather than the text
    /// itself, so the round-trip stays small regardless of script size.
    /// </summary>
    private static string Fingerprint(string command) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command)));

    private static bool LooksLikePnpCommand(string command)
    {
        return command.Contains("-PnP", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeSingleQuotedPowerShell(string value)
    {
        return value.Replace("'", "''");
    }
}
