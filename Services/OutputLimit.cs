using System.Globalization;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Caps tool output so a large result set cannot flood the caller's context.</summary>
internal static class OutputLimit
{
    private const int DefaultMaxChars = 50_000;

    // Below this the note would dominate what survives, so a smaller value is ignored rather than obeyed.
    internal const int MinimumMaxChars = 2_000;

    // Room set aside for the truncation note before any of the body is kept.
    private const int NoteBudget = 700;

    // Caller-supplied text is clamped so neither piece can crowd out the body or the warning.
    private const int MaxHintChars = 300;

    internal const string TruncationMarker = "\n\n[output truncated:";

    private const string DefaultHint =
        "Narrow the query (-PageSize, Select-Object, Where-Object, or a filter) and run it again.";

    public static int MaxChars =>
        int.TryParse(Environment.GetEnvironmentVariable("PNP_MCP_MAX_OUTPUT_CHARS"), out var chars) && chars >= MinimumMaxChars
            ? chars
            : DefaultMaxChars;

    /// <summary>Caps <paramref name="output"/>, keeping <paramref name="suffix"/> and counting it against the limit.</summary>
    // The returned string is never longer than MaxChars, whatever the caller passes.
    public static string Apply(string? output, string? narrowingHint = null, string? suffix = null)
    {
        var body = output ?? string.Empty;
        var limit = MaxChars;

        // Clamped first: a suffix or hint bigger than the budget would break the bound no matter how
        // much of the body were dropped.
        var tail = Truncate(suffix, limit / 4);
        var hint = string.IsNullOrWhiteSpace(narrowingHint) ? DefaultHint : Truncate(narrowingHint, MaxHintChars);

        if (body.Length + tail.Length <= limit)
        {
            return body + tail;
        }

        var head = CutAtLineBreak(body, limit - tail.Length - NoteBudget);

        // The note states how much was dropped, so shrinking the head changes the note. Rebuild it each
        // time instead of trimming once and leaving a stale count behind.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var note = BuildNote(body.Length - head.Length, body.Length, limit, hint);
            var total = head.Length + note.Length + tail.Length;

            if (total <= limit)
            {
                return head + note + tail;
            }

            var over = total - limit;
            if (head.Length <= over)
            {
                break;
            }

            head = head[..(head.Length - over)].TrimEnd();
        }

        // Only reachable if the warning alone cannot fit; the bound still holds.
        var last = head + BuildNote(body.Length - head.Length, body.Length, limit, hint) + tail;
        return last.Length <= limit ? last : last[..limit];
    }

    private static string BuildNote(int omitted, int total, int limit, string hint) =>
        $"{TruncationMarker} {Format(omitted)} of {Format(total)} characters omitted]\n\n" +
        "NOTE: The output above is incomplete, so it is not necessarily valid JSON and must not be " +
        $"parsed or summarised as if it were the whole result. {hint}\n" +
        $"Raise the cap with the PNP_MCP_MAX_OUTPUT_CHARS environment variable (currently {Format(limit)}).";

    // Cuts at the last line break so a row or JSON element is not split mid-token, unless doing so
    // would throw away most of what fits.
    private static string CutAtLineBreak(string body, int budget)
    {
        var head = body[..Math.Clamp(budget, 1, body.Length)];
        var lastBreak = head.LastIndexOf('\n');

        return (lastBreak > head.Length / 2 ? head[..lastBreak] : head).TrimEnd();
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        value.Length <= max ? value : value[..Math.Max(max, 0)];

    private static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
