using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PnPPowerShell.MCPServer.Tools;

[McpServerToolType]
internal class PnPPowerShellTools
{
    private const int DefaultTimeout = 120_000; // 2 minutes

    // Shared preamble injected into every script that needs the module. Fails fast with an
    // actionable message instead of letting Import-Module surface a raw PowerShell error.
    private const string ModuleCheckPreamble = """
        $ErrorActionPreference = 'Stop'
        if (-not (Get-Module -ListAvailable -Name PnP.PowerShell)) {
          Write-Output 'ERROR: The PnP.PowerShell module is not installed. Install it by running: Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force'
          exit 1
        }
        Import-Module PnP.PowerShell -ErrorAction Stop
        """;

    [McpServerTool(Name = "pnp_search_commands")]
    [Description("Searches PnP PowerShell commands using keyword matching against command names, verbs, and nouns. Use this tool first to find relevant commands before getting full command documentation.")]
    public async Task<string> SearchPnpCommands(
        [Description("One or more space-separated keywords to find relevant commands (e.g., \"site\", \"list item\", \"teams channel\", \"user add\")")] string query,
        [Description("Maximum number of results to return (default: 20, max: 100)")] int limit = 20)
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
            {{ModuleCheckPreamble}}
            $terms = {{termArray}}
            Get-Command -Module PnP.PowerShell |
              ForEach-Object {
                $cmd = $_
                $score = 0
                foreach ($term in $terms) {
                  if ($cmd.Name -like "*$term*") { $score += 10 }
                  if ($cmd.Verb -like "*$term*") { $score += 4 }
                  if ($cmd.Noun -like "*$term*") { $score += 6 }
                }
                if ($score -gt 0) {
                  [PSCustomObject]@{ Name = $cmd.Name; Verb = $cmd.Verb; Noun = $cmd.Noun; Score = $score }
                }
              } |
              Sort-Object -Property Score -Descending |
              Select-Object -First {{limit}} Name, Verb, Noun |
              ConvertTo-Json -Depth 5 -Compress
            """;

        var result = await RunPowerShellScriptAsync(script);

        return $"""
            {result}

            TIP: Before executing any of the commands, run the 'pnp_get_command_docs' tool to retrieve the full syntax, parameters, and examples.
            TIP: For complex tasks, break them into smaller steps and run commands incrementally using 'pnp_run_command'.
            """;
    }

    [McpServerTool(Name = "pnp_get_command_docs")]
    [Description("Gets detailed documentation for a specific PnP PowerShell command including syntax, parameters, and examples. Use this after searching for commands to understand how to use them correctly.")]
    public async Task<string> GetPnpCommandDocs(
        [Description("The full PnP PowerShell command name (e.g., \"Get-PnPWeb\", \"Connect-PnPOnline\", \"Get-PnPList\")")] string commandName)
    {
        var safeCommandName = EscapeSingleQuotedPowerShell(commandName ?? string.Empty);

        var script = $$"""
            {{ModuleCheckPreamble}}
            $helpText = Get-Help '{{safeCommandName}}' -Full | Out-String
            if ([string]::IsNullOrWhiteSpace($helpText)) {
              Write-Output "No documentation found for '{{safeCommandName}}'. Verify the command name using 'pnp_search_commands'."
            } else {
              Write-Output $helpText
            }
            """;

        return await RunPowerShellScriptAsync(script);
    }

    [McpServerTool(Name = "pnp_run_command")]
    [Description("Executes one or more PnP PowerShell commands and returns the result. Commands can be chained with semicolons or newlines. Always ensure you are connected first using Connect-PnPOnline. This tool can be used repeatedly to accomplish complex multi-step tasks.")]
    public async Task<string> RunPnpCommand(
        [Description("PnP PowerShell command(s) to execute (e.g., \"Get-PnPSite\", \"Get-PnPList | Select-Object Title, ItemCount\")")] string command)
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

        // Base64-encode the command to avoid escaping issues
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        var script = $$"""
            {{ModuleCheckPreamble}}
            $decodedCommand = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encoded}}'))
            try {
              $result = Invoke-Expression $decodedCommand
              if ($null -ne $result) {
                try {
                  $result | ConvertTo-Json -Depth 20 -Compress
                }
                catch {
                  $result | Out-String
                }
              } else {
                Write-Output 'Command completed successfully (no output).'
              }
            }
            catch {
              Write-Error "Command failed: $_"
              exit 1
            }
            """;

        return await RunPowerShellScriptAsync(script);
    }

    [McpServerTool(Name = "pnp_get_connection_status")]
    [Description("Checks the current PnP PowerShell connection status. Use this to verify if you are already connected to a SharePoint site or Microsoft 365 tenant before running commands.")]
    public async Task<string> GetPnpConnectionStatus()
    {
        var script = $$"""
            {{ModuleCheckPreamble}}
            try {
              $conn = Get-PnPConnection
              $info = @{
                Url = $conn.Url
                TenantAdminUrl = $conn.TenantAdminUrl
                ConnectionType = $conn.ConnectionType.ToString()
                PSCredential = if ($conn.PSCredential) { $conn.PSCredential.UserName } else { $null }
              }
              $info | ConvertTo-Json -Depth 5 -Compress
            }
            catch {
              Write-Output '{"connected":false,"message":"Not connected. Use Connect-PnPOnline to establish a connection. Run pnp_get_command_docs with commandName Connect-PnPOnline for usage details."}'
            }
            """;

        return await RunPowerShellScriptAsync(script);
    }

    [McpServerTool(Name = "pnp_get_best_practices")]
    [Description("Returns recommended best practices and guidance for using this MCP server with PnP PowerShell commands, including authentication, error handling, and execution tips.")]
    public async Task<string> GetPnpBestPractices()
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

            ## Authentication

            - Always start sessions with `Connect-PnPOnline`.
            - Prefer secure auth methods: `-Interactive`, certificate-based (`-ClientId`, `-Tenant`, `-Thumbprint`), or managed identity.
            - Avoid storing credentials in scripts; use Azure Key Vault or environment variables.
            - Check connection status before running commands to avoid auth errors.

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

    private static bool LooksLikePnpCommand(string command)
    {
        return command.Contains("-PnP", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeSingleQuotedPowerShell(string value)
    {
        return value.Replace("'", "''");
    }

    private static async Task<string> RunPowerShellScriptAsync(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return "Error: Could not launch 'pwsh'. Install PowerShell 7+ from https://aka.ms/powershell and ensure it is available on PATH.";
        }

        if (process is null)
        {
            return "Error: Failed to start PowerShell process. Ensure 'pwsh' (PowerShell 7+) is installed and available on PATH.";
        }

        using var cts = new CancellationTokenSource(DefaultTimeout);
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return "Error: Command timed out after 2 minutes. Consider breaking the operation into smaller steps.";
        }

        var output = (await stdOutTask).Trim();
        var error = (await stdErrTask).Trim();

        if (process.ExitCode != 0)
        {
            return string.IsNullOrWhiteSpace(error)
                ? $"Error: PowerShell command failed with exit code {process.ExitCode}."
                : $"Error: {error}";
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return string.IsNullOrWhiteSpace(output)
                ? error
                : $"{output}\n\nWarnings:\n{error}";
        }

        return string.IsNullOrWhiteSpace(output) ? "Command completed successfully." : output;
    }
}
