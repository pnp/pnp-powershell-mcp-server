using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>One command invocation found in a script.</summary>
internal sealed class ScriptCommand
{
    /// <summary>Resolved command name, or <c>&lt;dynamic&gt;</c> when it is not statically known.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Resolved verb; null for native executables and unnamed invocations.</summary>
    public string? Verb { get; set; }

    /// <summary>Whether the command implements ShouldProcess, so it can be simulated with -WhatIf.</summary>
    public bool SupportsWhatIf { get; set; }

    /// <summary>True when the command cannot be identified before it runs.</summary>
    public bool IsDynamic { get; set; }
}

/// <summary>What a script was found to contain.</summary>
internal sealed class ScriptAnalysis
{
    public string? ParseError { get; set; }

    // Nullable setters with non-null getters: an explicit JSON null would otherwise replace the
    // initializer and make every downstream LINQ call throw.
    private List<ScriptCommand> _commands = [];
    private List<string> _methodCalls = [];

    public List<ScriptCommand> Commands
    {
        get => _commands;
        set => _commands = value ?? [];
    }

    /// <summary>Names of .NET/CSOM methods the script invokes, e.g. DeleteObject or ExecuteQuery.</summary>
    public List<string> MethodCalls
    {
        get => _methodCalls;
        set => _methodCalls = value ?? [];
    }
}

/// <summary>Result of an analysis attempt; SessionError carries a session-level failure to report verbatim.</summary>
internal sealed record ScriptAnalysisResult(ScriptAnalysis? Analysis, string? SessionError);

/// <summary>Determines what a script invokes by parsing it with PowerShell's own parser.</summary>
internal static class ScriptAnalyzer
{
    public static async Task<ScriptAnalysisResult> AnalyzeAsync(
        PowerShellSession session,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        // InvokeCommand.GetCommand, not Get-Command: the latter treats the name as a wildcard, so "?" and "%" match several commands each.
        var script = $$"""
            $__pnpSrc = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encoded}}'))
            $__pnpParseErrors = $null
            $__pnpAst = [System.Management.Automation.Language.Parser]::ParseInput($__pnpSrc, [ref]$null, [ref]$__pnpParseErrors)
            $__pnpFound = @()
            $__pnpMethods = @()
            if ($__pnpAst) {
              $__pnpNodes = $__pnpAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)
              $__pnpFound = @(foreach ($__pnpNode in $__pnpNodes) {
                $__pnpName = $__pnpNode.GetCommandName()
                if ([string]::IsNullOrWhiteSpace($__pnpName)) {
                  [PSCustomObject]@{ name = '<dynamic>'; verb = $null; supportsWhatIf = $false; isDynamic = $true }
                } else {
                  $__pnpCmdInfo = $null
                  try { $__pnpCmdInfo = $ExecutionContext.InvokeCommand.GetCommand($__pnpName, [System.Management.Automation.CommandTypes]::All) } catch { $__pnpCmdInfo = $null }
                  $__pnpGuard = 0
                  while ($__pnpCmdInfo -and $__pnpCmdInfo.CommandType -eq 'Alias' -and $__pnpGuard -lt 10) {
                    $__pnpGuard++
                    $__pnpNext = $null
                    # ResolvedCommand is populated lazily and is null until the alias has been invoked once, so Definition is the reliable target.
                    if ($__pnpCmdInfo.ResolvedCommand) { $__pnpNext = $__pnpCmdInfo.ResolvedCommand }
                    elseif ($__pnpCmdInfo.Definition) {
                      try { $__pnpNext = $ExecutionContext.InvokeCommand.GetCommand([string]$__pnpCmdInfo.Definition, [System.Management.Automation.CommandTypes]::All) } catch { $__pnpNext = $null }
                    }
                    if (-not $__pnpNext) { break }
                    $__pnpCmdInfo = $__pnpNext
                  }
                  # An alias that could not be followed is as unverifiable as an indirect invocation.
                  $__pnpStillAlias = ($__pnpCmdInfo -and $__pnpCmdInfo.CommandType -eq 'Alias')
                  $__pnpEffective = if ($__pnpCmdInfo -and -not $__pnpStillAlias) { [string]$__pnpCmdInfo.Name } else { [string]$__pnpName }
                  $__pnpVerb = $null
                  if ($__pnpCmdInfo -and -not $__pnpStillAlias -and $__pnpCmdInfo.Verb) { $__pnpVerb = [string]$__pnpCmdInfo.Verb }
                  elseif (-not $__pnpStillAlias -and $__pnpEffective -match '^([A-Za-z]+)-') { $__pnpVerb = $Matches[1] }
                  $__pnpWhatIf = $false
                  if ($__pnpCmdInfo -and $__pnpCmdInfo.Parameters -and $__pnpCmdInfo.Parameters.ContainsKey('WhatIf')) { $__pnpWhatIf = $true }
                  [PSCustomObject]@{ name = $__pnpEffective; verb = $__pnpVerb; supportsWhatIf = $__pnpWhatIf; isDynamic = [bool]$__pnpStillAlias }
                }
              })
              # Method calls are collected separately because CSOM mutations are not CommandAst nodes.
              $__pnpMembers = $__pnpAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true)
              $__pnpMethods = @(foreach ($__pnpM in $__pnpMembers) {
                if ($__pnpM.Member -and $__pnpM.Member.Value) { [string]$__pnpM.Member.Value } else { '<dynamic>' }
              })
            }
            $__pnpParseMessage = $null
            if ($__pnpParseErrors -and @($__pnpParseErrors).Count -gt 0) { $__pnpParseMessage = @($__pnpParseErrors)[0].Message }
            [PSCustomObject]@{ parseError = $__pnpParseMessage; commands = $__pnpFound; methodCalls = $__pnpMethods } | ConvertTo-Json -Depth 6 -Compress
            Remove-Variable -Name __pnpSrc,__pnpParseErrors,__pnpAst,__pnpFound,__pnpMethods,__pnpNodes,__pnpNode,__pnpName,__pnpCmdInfo,__pnpGuard,__pnpNext,__pnpStillAlias,__pnpEffective,__pnpVerb,__pnpWhatIf,__pnpMembers,__pnpM,__pnpParseMessage -ErrorAction SilentlyContinue
            """;

        var raw = (await session.ExecuteAsync(script, timeout, cancellationToken, $"analyse\n{command}")).Trim();

        // A session-level failure is returned as-is rather than being read as an unanalysable script.
        if (raw.StartsWith("Error:", StringComparison.Ordinal))
        {
            return new ScriptAnalysisResult(null, raw);
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return new ScriptAnalysisResult(null, null);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(raw[start..(end + 1)], ScriptAnalyzerJsonContext.Default.ScriptAnalysis);
            return new ScriptAnalysisResult(parsed, null);
        }
        catch (JsonException)
        {
            return new ScriptAnalysisResult(null, null);
        }
    }
}

// Source-generated: the server publishes native AOT, where reflection-based serialization is unavailable.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScriptAnalysis))]
internal sealed partial class ScriptAnalyzerJsonContext : JsonSerializerContext;
