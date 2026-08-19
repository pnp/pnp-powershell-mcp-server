using System.Globalization;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Caps tool output so a large result set cannot flood the caller's context.</summary>
internal static class OutputLimit
{
    private const int DefaultMaxChars = 50_000;

    // Room reserved for the truncation note, so the returned response stays inside the configured cap.
    private const int NoteBudget = 700;

    // Below this the note would dominate what survives, so a smaller value is ignored rather than obeyed.
    internal const int MinimumMaxChars = 2_000;

    public static int MaxChars =>
        int.TryParse(Environment.GetEnvironmentVariable("PNP_MCP_MAX_OUTPUT_CHARS"), out var chars) && chars >= MinimumMaxChars
            ? chars
            : DefaultMaxChars;

    /// <summary>Caps <paramref name="output"/>, keeping <paramref name="suffix"/> and counting it against the limit.</summary>
    // The suffix exists so trailing material a caller must not lose -- TIP lines, an error hint -- is
    // reserved for up front rather than appended afterwards, which would push the response past the cap.
    public static string Apply(string? output, string? narrowingHint = null, string? suffix = null)
    {
        var body = output ?? string.Empty;
        var limit = MaxChars;

        // Never let a long suffix squeeze the body out entirely.
        var available = Math.Max(limit - (suffix?.Length ?? 0), limit / 2);

        if (body.Length <= available)
        {
            return body + suffix;
        }

        var head = body[..Math.Max(available - NoteBudget, available / 2)];

        // Cut at the last line break so a row or JSON element is not split mid-token, unless doing so
        // would throw away most of what fits.
        var lastBreak = head.LastIndexOf('\n');
        if (lastBreak > head.Length / 2)
        {
            head = head[..lastBreak];
        }

        head = head.TrimEnd();

        var note = $"""

            [output truncated: {Format(body.Length - head.Length)} of {Format(body.Length)} characters omitted]

            NOTE: The output above is incomplete, so it is not necessarily valid JSON and must not be
            parsed or summarised as if it were the whole result. {narrowingHint ?? "Narrow the query (-PageSize, Select-Object, Where-Object, or a filter) and run it again."}
            Raise the cap with the PNP_MCP_MAX_OUTPUT_CHARS environment variable (currently {Format(limit)}).
            """;

        // A caller-supplied hint can overrun the reserved room; the head yields, never the warning.
        var overflow = head.Length + note.Length + (suffix?.Length ?? 0) - limit;
        if (overflow > 0 && head.Length > overflow)
        {
            head = head[..(head.Length - overflow)].TrimEnd();
        }

        return head + note + suffix;
    }

    private static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
