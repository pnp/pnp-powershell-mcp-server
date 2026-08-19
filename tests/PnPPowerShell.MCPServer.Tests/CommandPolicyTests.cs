using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

public class CommandPolicyTests
{
    private static ScriptAnalysis Analysis(params (string Name, string? Verb)[] commands) =>
        new()
        {
            Commands = [.. commands.Select(c => new ScriptCommand { Name = c.Name, Verb = c.Verb })],
        };

    private static ScriptAnalysis WithMethods(params string[] methods) =>
        new()
        {
            Commands = [new ScriptCommand { Name = "Get-PnPList", Verb = "Get" }],
            MethodCalls = [.. methods],
        };

    [Fact]
    public void Enforce_reports_a_parse_error_regardless_of_mode()
    {
        var result = CommandPolicy.Enforce(new ScriptAnalysis { ParseError = "Missing closing brace." });

        Assert.NotNull(result);
        Assert.Contains("not valid PowerShell", result);
    }

    [Fact]
    public void Enforce_allows_a_write_when_read_only_is_off()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", null);

        Assert.Null(CommandPolicy.Enforce(Analysis(("Remove-PnPTenantSite", "Remove"))));
    }

    [Theory]
    [InlineData("Remove")]
    [InlineData("Clear")]
    [InlineData("Reset")]
    [InlineData("Uninstall")]
    [InlineData("Revoke")]
    [InlineData("Deny")]
    [InlineData("Restore")]
    [InlineData("Move")]
    [InlineData("Rename")]
    [InlineData("Disable")]
    public void FindNeedingConfirmation_flags_destructive_verbs(string verb)
    {
        Assert.Equal(verb + "-PnPThing", CommandPolicy.FindNeedingConfirmation(Analysis((verb + "-PnPThing", verb))));
    }

    [Theory]
    [InlineData("Get")]
    [InlineData("Set")]
    [InlineData("Add")]
    [InlineData("New")]
    [InlineData("Enable")]
    [InlineData("Grant")]
    public void FindNeedingConfirmation_ignores_non_destructive_verbs(string verb)
    {
        Assert.Null(CommandPolicy.FindNeedingConfirmation(Analysis((verb + "-PnPThing", verb))));
    }

    [Fact]
    public void FindNeedingConfirmation_flags_an_indirect_invocation()
    {
        // A dynamic node plus a harmless Get-Command is how "& (Get-Command Remove-PnPTenantSite)"
        // parses, so keying only on verbs would let it through unconfirmed.
        var analysis = new ScriptAnalysis
        {
            Commands =
            [
                new ScriptCommand { Name = "<dynamic>", Verb = null, IsDynamic = true },
                new ScriptCommand { Name = "Get-Command", Verb = "Get" },
            ],
        };

        var flagged = CommandPolicy.FindNeedingConfirmation(analysis);

        Assert.NotNull(flagged);
        Assert.Contains("indirectly invoked", flagged);
    }

    [Theory]
    [InlineData("ExecuteQuery")]
    [InlineData("ExecuteQueryAsync")]
    [InlineData("DeleteObject")]
    [InlineData("RecycleObject")]
    public void FindNeedingConfirmation_flags_state_changing_method_calls(string method)
    {
        var flagged = CommandPolicy.FindNeedingConfirmation(WithMethods(method));

        Assert.NotNull(flagged);
        Assert.Contains(method, flagged);
    }

    [Theory]
    [InlineData("ToString")]
    [InlineData("Trim")]
    [InlineData("Contains")]
    [InlineData("Add")]
    [InlineData("Substring")]
    public void FindNeedingConfirmation_ignores_harmless_method_calls(string method)
    {
        // Add in particular: building a local collection is ordinary and changes nothing in Microsoft 365.
        Assert.Null(CommandPolicy.FindNeedingConfirmation(WithMethods(method)));
    }

    [Fact]
    public void FindNeedingConfirmation_flags_a_dynamic_method_name()
    {
        Assert.NotNull(CommandPolicy.FindNeedingConfirmation(WithMethods("<dynamic>")));
    }

    [Theory]
    [InlineData("Get")]
    [InlineData("Test")]
    [InlineData("Find")]
    [InlineData("Search")]
    [InlineData("Measure")]
    [InlineData("Resolve")]
    [InlineData("Read")]
    [InlineData("Export")]
    [InlineData("Convert")]
    [InlineData("ConvertTo")]
    [InlineData("ConvertFrom")]
    [InlineData("Format")]
    [InlineData("Write")]
    [InlineData("Show")]
    [InlineData("Compare")]
    [InlineData("Connect")]
    [InlineData("Disconnect")]
    [InlineData("Select")]
    [InlineData("Where")]
    [InlineData("Sort")]
    [InlineData("Group")]
    [InlineData("ForEach")]
    [InlineData("Out")]
    [InlineData("Join")]
    [InlineData("Split")]
    public void Enforce_allows_read_verbs_in_read_only_mode(string verb)
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        Assert.Null(CommandPolicy.Enforce(Analysis((verb + "-PnPThing", verb))));
    }

    [Theory]
    [InlineData("Set")]
    [InlineData("Remove")]
    [InlineData("Add")]
    [InlineData("New")]
    [InlineData("Clear")]
    [InlineData("Invoke")]
    [InlineData("Update")]
    [InlineData("Move")]
    [InlineData("Enable")]
    [InlineData("Disable")]
    [InlineData("Grant")]
    [InlineData("Revoke")]
    [InlineData("Copy")]
    [InlineData("Import")]
    [InlineData("Restore")]
    [InlineData("Reset")]
    [InlineData("Rename")]
    [InlineData("Start")]
    [InlineData("Stop")]
    [InlineData("Register")]
    [InlineData("Unregister")]
    [InlineData("Install")]
    [InlineData("Uninstall")]
    [InlineData("Publish")]
    [InlineData("Submit")]
    public void Enforce_refuses_change_verbs_in_read_only_mode(string verb)
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        var result = CommandPolicy.Enforce(Analysis((verb + "-PnPThing", verb)));

        Assert.NotNull(result);
        Assert.Contains("not read-only", result);
    }

    [Fact]
    public void Enforce_refuses_a_verbless_command_in_read_only_mode()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        // A native executable has no verb to classify, so it cannot be shown to be read-only.
        var result = CommandPolicy.Enforce(Analysis(("pwsh.exe", null)));

        Assert.NotNull(result);
        Assert.Contains("not read-only", result);
    }

    [Fact]
    public void Enforce_refuses_an_indirect_invocation_in_read_only_mode()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        var analysis = new ScriptAnalysis
        {
            Commands = [new ScriptCommand { Name = "<dynamic>", IsDynamic = true }],
        };

        var result = CommandPolicy.Enforce(analysis);

        Assert.NotNull(result);
        Assert.Contains("invokes a command indirectly", result);
    }

    [Fact]
    public void Enforce_refuses_a_csom_commit_in_read_only_mode()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        // ExecuteQuery is the commit point for every CSOM mutation, so it is refused even though every
        // surrounding command is a read.
        var result = CommandPolicy.Enforce(WithMethods("ExecuteQuery"));

        Assert.NotNull(result);
        Assert.Contains("can change state", result);
    }

    [Fact]
    public void Enforce_allows_a_local_collection_add_in_read_only_mode()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        Assert.Null(CommandPolicy.Enforce(WithMethods("Add")));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("")]
    public void ReadOnlyMode_requires_the_exact_literal_true(string value)
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", value);

        Assert.False(CommandPolicy.ReadOnlyMode);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void ReadOnlyMode_is_case_insensitive(string value)
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", value);

        Assert.True(CommandPolicy.ReadOnlyMode);
    }
}
