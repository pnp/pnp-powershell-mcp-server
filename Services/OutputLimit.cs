using System.Globalization;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Caps tool output so a large result set cannot flood the caller's context.</summary>
internal static class OutputLimit
{
    private const int DefaultMaxChars = 50_000;

    // Room reserved for the truncation note, so the returned response stays inside the configured cap
    // rather than exceeding it by however long the note happens to be.
    private const int NoteBudget = 700;

    // Below this the note would dominate what survives, so a smaller value is ignored rather than obeyed.
    private const int MinimumMaxChars = 2_000;

    public static int MaxChars =>
        int.TryParse(Environment.GetEnvironmentVariable("PNP_MCP_MAX_OUTPUT_CHARS"), out var chars) && chars >= MinimumMaxChars
            ? chars
            : DefaultMaxChars;

    /// <summary>Returns the output unchanged, or its head plus an explicit note about what was dropped.</summary>
    public static string Apply(string? output, string? narrowingHint = null)
    {
        if (string.IsNullOrEmpty(output))
        {
            return output ?? string.Empty;
        }

        var limit = MaxChars;
        if (output.Length <= limit)
        {
            return output;
        }

        var head = output[..(limit - NoteBudget)];

        // Cut at the last line break so a row or JSON element is not split mid-token, unless doing so
        // would throw away most of what fits.
        var lastBreak = head.LastIndexOf('\n');
        if (lastBreak > head.Length / 2)
        {
            head = head[..lastBreak];
        }

        head = head.TrimEnd();
        var omitted = output.Length - head.Length;

        var note = $"""

            [output truncated: {Format(omitted)} of {Format(output.Length)} characters omitted]

            NOTE: The output above is incomplete, so it is not necessarily valid JSON and must not be
            parsed or summarised as if it were the whole result. {narrowingHint ?? "Narrow the query (-PageSize, Select-Object, Where-Object, or a filter) and run it again."}
            Raise the limit with the PNP_MCP_MAX_OUTPUT_CHARS environment variable (currently {Format(limit)}).
            """;

        // A caller-supplied hint could overrun the reserved room; trimming the head keeps the promise.
        var overflow = head.Length + note.Length - limit;
        if (overflow > 0 && head.Length > overflow)
        {
            head = head[..(head.Length - overflow)].TrimEnd();
        }

        return head + note;
    }

    private static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
