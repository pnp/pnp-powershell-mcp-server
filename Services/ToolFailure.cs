using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Sets isError on a text result that reports a failure or a refusal.</summary>
internal static class ToolFailure
{
    // A declined confirmation is left out: that is the gate working.
    private static readonly string[] Prefixes = ["Error:", "Blocked:"];

    /// <summary>Runs inside a task too, so a failed command does not finish as a successful one.</summary>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) => Mark(await next(request, cancellationToken));

    internal static CallToolResult Mark(CallToolResult result)
    {
        if (IsFailure(result))
        {
            result.IsError = true;
        }

        return result;
    }

    // Structured tools set the flag themselves.
    internal static bool IsFailure(CallToolResult result) =>
        result.IsError is not true &&
        result.StructuredContent is null &&
        result.Content is [TextContentBlock { Text: { } text }, ..] &&
        (Prefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)) || IsCancelledByTheServer(text));

    // A reset session or an expired approval also says "Cancelled:", and nobody chose those.
    private static bool IsCancelledByTheServer(string text) =>
        text.StartsWith("Cancelled:", StringComparison.Ordinal) &&
        !text.Contains("confirmed", StringComparison.Ordinal);
}
