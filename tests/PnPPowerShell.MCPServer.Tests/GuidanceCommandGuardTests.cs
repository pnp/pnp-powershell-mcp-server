using PnPPowerShell.MCPServer.Services;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// Every cmdlet and parameter name this repo hands to a person must exist in the command corpus. Covers the
/// fenced PowerShell blocks in best-practices.md and the generated NEXT STEP line of the preflight report.
/// Checks names only: behaviour claims and environment variable names are out of reach of cmdlet metadata.
/// </summary>
public partial class GuidanceCommandGuardTests
{
    private sealed record Invocation(string Origin, string Command, IReadOnlyList<string> Parameters);

    // Engine-supplied on every cmdlet, so the corpus does not list them. -WhatIf and -Confirm are deliberately
    // absent: PnP cmdlets do not uniformly support them, and the corpus cannot say which do.
    private static readonly HashSet<string> CommonParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Debug", "ErrorAction", "ErrorVariable", "InformationAction", "InformationVariable", "OutBuffer",
        "OutVariable", "PipelineVariable", "ProgressAction", "Verbose", "WarningAction", "WarningVariable",
    };

    [GeneratedRegex(@"\b[A-Z][a-z]+-[A-Z][A-Za-z]+\b")]
    private static partial Regex CommandName();

    [GeneratedRegex(@"(?<=\s)-([A-Za-z][A-Za-z]*)\b")]
    private static partial Regex ParameterToken();

    [GeneratedRegex(@"(?<=\w)\.(?=\s|$)|\r?\n")]
    private static partial Regex SentenceEnd();

    [Fact]
    public void Every_parameter_in_the_guidance_code_blocks_exists_on_its_cmdlet()
    {
        var blocks = FencedPowerShellBlocks(EmbeddedGuidance());
        Assert.NotEmpty(blocks);

        var invocations = blocks.SelectMany((block, i) => Parse(block, $"best-practices.md block {i + 1}")).ToList();

        AssertKnown(invocations, minimumChecked: 10);
    }

    [Fact]
    public void Every_parameter_in_a_generated_next_step_exists_on_its_cmdlet()
    {
        var invocations = PreflightScenarios()
            .SelectMany(s => FromProse(NextStep(ConnectionPreflight.Render(s.Facts)), $"NEXT STEP when {s.Name}"))
            .ToList();

        Assert.Contains(invocations, i => i.Command == "Connect-PnPOnline");
        Assert.Contains(invocations, i => i.Command == "Register-PnPEntraIDAppForInteractiveLogin");

        AssertKnown(invocations, minimumChecked: 5);
    }

    private static void AssertKnown(IReadOnlyList<Invocation> invocations, int minimumChecked)
    {
        var version = CommandCorpus.ModuleVersion ?? "unknown";
        var checkedCount = 0;
        var failures = new List<string>();

        foreach (var invocation in invocations)
        {
            if (CommandCorpus.Lookup(invocation.Command) is not { } command)
            {
                continue;
            }

            checkedCount++;

            foreach (var parameter in invocation.Parameters)
            {
                if (CommonParameters.Contains(parameter)
                    || command.Parameters.Any(p => string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                failures.Add(
                    $"{invocation.Origin}: '{invocation.Command} -{parameter}' names a parameter {command.Name} does not have " +
                    $"in the corpus (PnP.PowerShell {version}). Either the guidance is wrong or data/pnp-index.json is stale.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(checkedCount >= minimumChecked, $"Only {checkedCount} commands resolved against the corpus; the extraction is probably broken.");
    }

    private static string EmbeddedGuidance()
    {
        using var stream = typeof(CommandCorpus).Assembly.GetManifestResourceStream("best-practices.md")
            ?? throw new InvalidOperationException("best-practices.md is not embedded.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private static List<string> FencedPowerShellBlocks(string document)
    {
        var blocks = new List<string>();
        List<string>? current = null;

        foreach (var line in document.Split('\n'))
        {
            var trimmed = line.Trim();

            if (current is null)
            {
                if (trimmed.StartsWith("```powershell", StringComparison.OrdinalIgnoreCase))
                {
                    current = [];
                }
            }
            else if (trimmed == "```")
            {
                blocks.Add(string.Join('\n', current));
                current = null;
            }
            else
            {
                current.Add(line);
            }
        }

        return blocks;
    }

    // Placeholders such as <app id> produce parse errors, and the AST still carries every parameter.
    private static IEnumerable<Invocation> Parse(string script, string origin)
    {
        var ast = Parser.ParseInput(script, out _, out _);

        foreach (var node in ast.FindAll(n => n is CommandAst, searchNestedScriptBlocks: true).Cast<CommandAst>())
        {
            if (node.GetCommandName() is { } name)
            {
                yield return new Invocation(
                    origin,
                    name,
                    [.. node.CommandElements.OfType<CommandParameterAst>().Select(p => p.ParameterName)]);
            }
        }
    }

    // Prose, not a script: each command owns the -Name tokens up to the next command, sentence end, or line end.
    private static IEnumerable<Invocation> FromProse(string text, string origin)
    {
        var names = CommandName().Matches(text);

        for (var i = 0; i < names.Count; i++)
        {
            var start = names[i].Index + names[i].Length;
            var end = i + 1 < names.Count ? names[i + 1].Index : text.Length;
            var sentence = SentenceEnd().Match(text, start);

            if (sentence.Success && sentence.Index < end)
            {
                end = sentence.Index;
            }

            yield return new Invocation(
                origin,
                names[i].Value,
                [.. ParameterToken().Matches(text[start..end]).Select(m => m.Groups[1].Value)]);
        }
    }

    private static string NextStep(string report)
    {
        var index = report.IndexOf("NEXT STEP:", StringComparison.Ordinal);
        Assert.True(index >= 0, "The preflight report has no NEXT STEP line.");

        return report[index..];
    }

    private static IEnumerable<(string Name, PreflightFacts Facts)> PreflightScenarios()
    {
        const string app = "11111111-1111-4111-8111-111111111111";
        const string site = "https://contoso.sharepoint.com/sites/marketing";
        var working = new EnvironmentFacts { PwshVersion = "7.5.4", ModuleVersion = "3.4.1", ModuleVersionCount = 1 };
        var persisted = new PersistedLogin { Url = "https://contoso.sharepoint.com", ClientId = app, Enabled = true };

        static PreflightFacts Facts(EnvironmentFacts environment, SessionFacts? session = null, string? error = null, AuthFacts? auth = null, string? target = null) =>
            new("default", environment, session, error, auth ?? AuthFacts.None, target);

        yield return ("pwsh is missing", Facts(new EnvironmentFacts { ProbeError = "not found" }));
        yield return ("pwsh is broken", Facts(new EnvironmentFacts { PwshLaunched = true, ProbeError = "no answer" }));
        yield return ("the module is missing", Facts(new EnvironmentFacts { PwshVersion = "7.5.4" }));
        yield return ("the session is busy", Facts(working, error: "Error: busy running another command"));
        yield return ("the session failed", Facts(working, error: "Error: gone"));
        yield return ("the session is unreadable", Facts(working));
        yield return ("nothing is registered", Facts(working, new SessionFacts()));
        yield return ("nothing is registered for a target", Facts(working, new SessionFacts(), target: site));
        yield return ("a login and token are persisted", Facts(working, new SessionFacts(), auth: new AuthFacts([persisted], true, null, null, null, null)));
        yield return ("a login is persisted without a token", Facts(working, new SessionFacts(), auth: new AuthFacts([persisted], false, null, null, null, null), target: site));
        yield return ("the client id comes from the environment", Facts(working, new SessionFacts(), auth: new AuthFacts([], false, "AZURE_CLIENT_ID", app, null, null)));
        yield return ("a certificate is configured", Facts(working, new SessionFacts(), auth: new AuthFacts([], false, null, null, @"C:\certs\pnp.pfx", null), target: site));
        yield return ("the connection has no URL", Facts(working, new SessionFacts { Connected = true, HelpUri = "https://x" }));
        yield return ("a device login targets a site", Facts(working, new SessionFacts { Connected = true, Url = site, ConnectionMethod = "DeviceLogin", HelpUri = "https://x" }));
        yield return ("a site connection is ready", Facts(working, new SessionFacts { Connected = true, Url = site, HelpUri = "https://x" }));
        yield return ("an admin connection is ready", Facts(working, new SessionFacts { Connected = true, Url = "https://contoso-admin.sharepoint.com" }));
    }
}
