using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Parses real scripts through a real pwsh session; skipped when PnP.PowerShell is unavailable.</summary>
public class ScriptAnalyzerTests : IAsyncLifetime
{
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(3);

    private readonly PowerShellSession _session = new("test-analyzer");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _session.DisposeAsync();

    private async Task<ScriptAnalysis> Analyze(string command)
    {
        var (analysis, sessionError) = await ScriptAnalyzer.AnalyzeAsync(_session, command, Generous);

        Assert.Null(sessionError);
        Assert.NotNull(analysis);
        return analysis;
    }

    [RequiresPnPFact]
    public async Task A_cmdlet_is_resolved_with_its_verb()
    {
        var analysis = await Analyze("Get-PnPWeb");

        var command = Assert.Single(analysis.Commands);
        Assert.Equal("Get-PnPWeb", command.Name);
        Assert.Equal("Get", command.Verb);
        Assert.False(command.IsDynamic);
    }

    [RequiresPnPFact]
    public async Task An_alias_is_resolved_to_its_target_before_it_has_ever_been_invoked()
    {
        // AliasInfo.ResolvedCommand is populated lazily and is null until the alias has been used once,
        // so resolution has to go through Definition. Getting this wrong let "rm -Recurse -Force" run
        // unconfirmed, and did so only sometimes.
        var analysis = await Analyze("rm -Force C:\\nope.txt");

        var command = Assert.Single(analysis.Commands);
        Assert.Equal("Remove-Item", command.Name);
        Assert.Equal("Remove", command.Verb);
    }

    [RequiresPnPFact]
    public async Task Wildcard_shaped_aliases_resolve_to_a_single_command()
    {
        // Get-Command treats "?" and "%" as wildcards and returns several matches each, which came back
        // as JSON arrays instead of one resolved command.
        var analysis = await Analyze("Get-ChildItem C:\\ | ? { $_.Name } | % { $_ }");

        Assert.Contains(analysis.Commands, c => c.Name == "Where-Object" && c.Verb == "Where");
        Assert.Contains(analysis.Commands, c => c.Name == "ForEach-Object" && c.Verb == "ForEach");
        Assert.DoesNotContain(analysis.Commands, c => c.Name.Contains("Invoke-History", StringComparison.Ordinal));
    }

    [RequiresPnPFact]
    public async Task An_indirect_invocation_is_reported_as_dynamic()
    {
        var analysis = await Analyze("& (Get-Command Remove-PnPTenantSite) -Url https://x");

        Assert.Contains(analysis.Commands, c => c.IsDynamic);
    }

    [RequiresPnPFact]
    public async Task A_destructive_verb_inside_a_string_is_not_treated_as_an_invocation()
    {
        var analysis = await Analyze("\"the words Remove-Item in a string\"; Get-Command Get-PnPWeb");

        Assert.DoesNotContain(analysis.Commands, c => c.Verb == "Remove");
    }

    [RequiresPnPFact]
    public async Task Method_calls_are_collected_separately_from_commands()
    {
        // CSOM mutations are not CommandAst nodes, so they are invisible to the verb check.
        var analysis = await Analyze("$l = Get-PnPList -Identity Docs; $l.DeleteObject(); (Get-PnPContext).ExecuteQuery()");

        Assert.Contains("DeleteObject", analysis.MethodCalls);
        Assert.Contains("ExecuteQuery", analysis.MethodCalls);
    }

    [RequiresPnPFact]
    public async Task A_static_dotnet_call_is_collected_as_a_method_call()
    {
        var analysis = await Analyze("[System.IO.File]::Delete('C:\\nope.txt'); Get-PnPWeb");

        Assert.Contains("Delete", analysis.MethodCalls);
    }

    [RequiresPnPFact]
    public async Task A_single_method_call_still_arrives_as_a_list()
    {
        // A one-element array would break deserialization if PowerShell unrolled it to a scalar, which
        // would silently disable the method-call check.
        var analysis = await Analyze("(Get-Command Get-PnPWeb).Name.ToString()");

        Assert.Single(analysis.MethodCalls);
        Assert.Equal("ToString", analysis.MethodCalls[0]);
    }

    [RequiresPnPFact]
    public async Task A_parse_error_is_reported()
    {
        var analysis = await Analyze("Get-PnPWeb | Where-Object {");

        Assert.False(string.IsNullOrWhiteSpace(analysis.ParseError));
    }

    [RequiresPnPFact]
    public async Task An_empty_script_yields_no_commands()
    {
        var analysis = await Analyze("");

        Assert.Empty(analysis.Commands);
        Assert.Empty(analysis.MethodCalls);
    }

    [RequiresPnPFact]
    public async Task A_native_executable_has_no_verb()
    {
        var analysis = await Analyze("pwsh -c 1");

        var command = Assert.Single(analysis.Commands);
        Assert.Null(command.Verb);
    }

    [RequiresPnPFact]
    public async Task WhatIf_support_is_reported_per_command()
    {
        var analysis = await Analyze("Remove-Item C:\\nope.txt");

        Assert.True(Assert.Single(analysis.Commands).SupportsWhatIf);
    }

    [RequiresPnPFact]
    public async Task Analysis_does_not_leak_variables_into_the_session()
    {
        await Analyze("Get-PnPWeb");

        var probe = await _session.ExecuteAsync("\"leaked=[$__pnpFound][$__pnpMethods][$__pnpVerb]\"", Generous);

        Assert.Contains("leaked=[][][]", probe);
    }
}
