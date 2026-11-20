using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Completes the cmdlet name in pnp://cmdlet/{name} from the vendored index.</summary>
internal static class CmdletCompletion
{
    internal const string Template = "pnp://cmdlet/{name}";

    // The protocol's own ceiling for one completion answer.
    private const int MaxValues = 100;

    // The SDK appends the [AllowedValues] completions to whatever this returns.
    internal static ValueTask<CompleteResult> HandleAsync(RequestContext<CompleteRequestParams> request, CancellationToken cancellationToken)
    {
        var result = new CompleteResult();

        if (request.Params is { Ref: ResourceTemplateReference { Uri: Template }, Argument: { Name: "name" } argument })
        {
            var matches = Match(argument.Value);

            result.Completion = new Completion
            {
                Values = [.. matches.Take(MaxValues)],
                Total = matches.Count,
                HasMore = matches.Count > MaxValues,
            };
        }

        return ValueTask.FromResult(result);
    }

    /// <summary>Names starting with the text first, then names containing it, so "tenantsite" finds Get-PnPTenantSite.</summary>
    internal static List<string> Match(string? typed)
    {
        var text = typed?.Trim() ?? string.Empty;
        var commands = CommandIndex.Commands;

        return
        [
            .. commands.Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase)),
            .. commands.Where(c => text.Length > 0 &&
                !c.StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
                c.Contains(text, StringComparison.OrdinalIgnoreCase)),
        ];
    }
}
