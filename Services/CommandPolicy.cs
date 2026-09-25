namespace PnPPowerShell.MCPServer.Services;

/// <summary>Decides whether a script may run, from what <see cref="ScriptAnalyzer"/> found in it.</summary>
internal static class CommandPolicy
{
    // Read verbs, plus Connect/Disconnect (without them the mode could not authenticate) and the
    // pipeline-shaping verbs (Select-Object and friends are CommandAst nodes, so omitting them would
    // reject even "Get-PnPList | Select-Object Title"). Documented in best-practices.md.
    private static readonly HashSet<string> ReadOnlyVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connect", "Disconnect",
        "Get", "Test", "Find", "Search", "Measure", "Resolve", "Show", "Compare", "Read",
        "Export", "Convert", "ConvertTo", "ConvertFrom",
        "Format", "Out", "Select", "Where", "ForEach", "Sort", "Group", "Join", "Split", "Write",
    };

    // Verbs that destroy, overwrite or revoke. Ordinary mutating verbs (Set, Add, New, Enable, Grant)
    // are excluded so the prompt stays rare enough to be read rather than dismissed.
    private static readonly HashSet<string> DestructiveVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Remove", "Clear", "Reset", "Uninstall", "Revoke", "Deny", "Restore", "Move", "Rename", "Disable",
        "Unregister", "Unpublish", "Merge",
    };

    // Each runs code the parse cannot see into: code passed as data, a module's own code, or another process.
    private static readonly HashSet<string> CodeRunners = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoke-Expression", "Invoke-Command", "Start-Job", "Start-ThreadJob", "Add-Type",
        "Start-Process", "Invoke-Item", "Import-Module",
    };

    // Each can define a command from data, which is then called before any analysis could see its body.
    private static readonly HashSet<string> Definers = new(StringComparer.OrdinalIgnoreCase)
    {
        "New-Item", "Set-Item", "Set-Content", "Add-Content", "Copy-Item", "New-Alias", "Set-Alias", "Import-Alias", "Import-Module", "New-Module",
    };

    // Generic REST: a delete through these carries no destructive verb.
    private static readonly HashSet<string> RestCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoke-PnPSPRestMethod", "Invoke-PnPGraphMethod",
    };

    // Deliberately narrow. "Execute" covers ExecuteQuery, the commit point for every CSOM mutation, so
    // a change made through the object model is caught whether or not the mutating call is recognised.
    // Broader prefixes such as Add or Set would flag $results.Add($row) on a local collection, which is
    // an ordinary reporting pattern and changes nothing in Microsoft 365. "Invoke" runs a script block.
    private static readonly string[] MutatingMethodPrefixes = ["Execute", "Delete", "Recycle", "Invoke"];

    // Script blocks built from strings, and Process.Start; exact, so CreateDirectory and StartNew stay allowed.
    private static readonly HashSet<string> ExactMethods = new(StringComparer.OrdinalIgnoreCase) { "Create", "NewScriptBlock", "Start" };

    public static bool ReadOnlyMode =>
        string.Equals(Environment.GetEnvironmentVariable("PNP_MCP_READONLY"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The message to return when the script may not run, or null when it may.</summary>
    public static string? Enforce(ScriptAnalysis analysis)
    {
        if (!string.IsNullOrWhiteSpace(analysis.ParseError))
        {
            return $"Error: The command is not valid PowerShell and was not run.\n{analysis.ParseError}";
        }

        if (!ReadOnlyMode)
        {
            return null;
        }

        // Anything that cannot be named cannot be vouched for.
        if (analysis.Commands.Any(c => c.IsDynamic))
        {
            return
                "Blocked: read-only mode is on (PNP_MCP_READONLY=true) and this script invokes a command indirectly, " +
                "so what it would run cannot be verified. Rewrite it to call commands by name.";
        }

        var mutatingMethods = FindMutatingMethods(analysis);
        if (mutatingMethods.Count > 0)
        {
            return
                $"Blocked: read-only mode is on (PNP_MCP_READONLY=true). These method calls can change state: {string.Join(", ", mutatingMethods)}.\n" +
                "Use PnP cmdlets instead of the client object model; CSOM changes commit through ExecuteQuery, which is not permitted in this mode.";
        }

        var disallowed = analysis.Commands
            .Where(c => c.Verb is null || !ReadOnlyVerbs.Contains(c.Verb))
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (disallowed.Count > 0)
        {
            return
                $"Blocked: read-only mode is on (PNP_MCP_READONLY=true). These commands are not read-only: {string.Join(", ", disallowed)}.\n" +
                "Unset PNP_MCP_READONLY to allow changes.";
        }

        return null;
    }

    /// <summary>Describes the first thing that must not run unconfirmed, or null if there is none.</summary>
    public static string? FindNeedingConfirmation(ScriptAnalysis analysis)
    {
        // Indirect invocation counts: "& (Get-Command Remove-PnPTenantSite)" parses to a dynamic node
        // plus a harmless Get-Command, so keying only on verbs would let it through unconfirmed.
        // Only alongside a definer, so a mistyped cmdlet name still fails fast instead of prompting.
        var defines = analysis.Commands.Any(c => Definers.Contains(c.Name));

        foreach (var command in analysis.Commands)
        {
            if (command.IsDynamic)
            {
                return "an indirectly invoked command, which cannot be identified before it runs";
            }

            if (defines && command.Unresolved)
            {
                return $"{command.Name}, which this script defines as it runs, so what it runs cannot be checked";
            }

            // A native program, a script file or an unresolvable name: nothing to classify, as read-only mode already concludes.
            if (command.Verb is null)
            {
                return $"{command.Name}, which has no verb, so what it does cannot be judged before it runs";
            }

            if (DestructiveVerbs.Contains(command.Verb))
            {
                return command.Name;
            }

            if (CodeRunners.Contains(command.Name))
            {
                return $"{command.Name}, which runs code that cannot be identified before it runs";
            }

            if (RestCommands.Contains(command.Name) &&
                command.Method is { } httpMethod &&
                (httpMethod == "<dynamic>" || httpMethod.Equals("Delete", StringComparison.OrdinalIgnoreCase)))
            {
                return $"{command.Name} -Method {httpMethod}";
            }
        }

        // CSOM mutations are method calls, not commands, so they are invisible to the verb check.
        var method = FindMutatingMethods(analysis).FirstOrDefault();
        return method is null ? null : $"a method call that can change state ({method})";
    }

    private static List<string> FindMutatingMethods(ScriptAnalysis analysis) =>
        [.. analysis.MethodCalls
            .Where(m => m == "<dynamic>" || ExactMethods.Contains(m) || MutatingMethodPrefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}
