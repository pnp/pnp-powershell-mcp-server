using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>A JSON result set held for paging, keyed by an opaque cursor.</summary>
internal sealed class HeldResultSet
{
    public required string Cursor { get; init; }

    /// <summary>The rows kept for paging, which may be a prefix of the result set.</summary>
    public required IReadOnlyList<string> Rows { get; init; }

    /// <summary>Rows the command actually returned, counted whether or not they were kept.</summary>
    public required int TotalRows { get; init; }

    public required IReadOnlyList<string> Fields { get; init; }
    public required int RawLength { get; init; }

    /// <summary>True when the result was too large to hold whole and only a prefix is pageable.</summary>
    public bool Partial => TotalRows > Rows.Count;
}

/// <summary>Summarises an oversized JSON array into pages instead of truncating it.</summary>
internal static class ResultSummary
{
    // Reserved for the header, the field list and the continuation footer before any row is measured.
    private const int Overhead = 2_000;

    // The list repeats on every page, so naming fifty-odd fields stops being useful past the first few.
    private const int MaxListedFields = 25;

    // Bounds the field line in characters as well as in count, so Overhead cannot be overrun by long names.
    private const int MaxFieldChars = 600;

    /// <summary>Ceiling on what a session pins in memory. Rows past it are counted, not kept.</summary>
    private const int MaxHeldChars = 8_000_000;

    /// <summary>Row ceiling too: many tiny rows cost more than their characters.</summary>
    private const int MaxHeldRows = 100_000;

    /// <summary>Captures a JSON array of two or more elements; null for anything else.</summary>
    public static HeldResultSet? TryCapture(string? output)
    {
        var text = output?.TrimStart() ?? string.Empty;
        if (!text.StartsWith('['))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var rows = new List<string>();
            var fields = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;
            var held = 0;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                total++;

                // Counting continues past the ceilings so the reported total stays true.
                if (held < MaxHeldChars && rows.Count < MaxHeldRows)
                {
                    var row = element.GetRawText();
                    rows.Add(row);
                    held += row.Length;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (seen.Add(property.Name))
                    {
                        fields.Add(property.Name);
                    }
                }
            }

            // A single row cannot be paged into anything smaller, so truncation is the honest answer there.
            return rows.Count < 2
                ? null
                : new HeldResultSet
                {
                    Cursor = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5)),
                    Rows = rows,
                    TotalRows = total,
                    Fields = fields,
                    RawLength = text.Length,
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Renders one page: what the whole result set is, then as many rows from <paramref name="offset"/> as fit.</summary>
    public static string Render(HeldResultSet held, int offset, string sessionId)
    {
        // Paging walks held rows; every reported figure is the true total.
        var pageable = held.Rows.Count;
        var total = held.TotalRows;
        offset = Math.Clamp(offset, 0, Math.Max(pageable - 1, 0));

        var budget = Math.Max(OutputLimit.MaxChars - Overhead, 500);
        var taken = 0;
        var used = 0;

        while (offset + taken < pageable && used + held.Rows[offset + taken].Length + 1 <= budget)
        {
            used += held.Rows[offset + taken].Length + 1;
            taken++;
        }

        // A row wider than a page would be cut mid-token, so skip it.
        var oversized = taken == 0 && offset < pageable;
        var end = offset + (oversized ? 1 : taken);

        var sb = new StringBuilder();
        sb.AppendLine(
            $"Result set: {N(total)} rows, summarised because the full output is {N(held.RawLength)} characters " +
            $"and the cap is {N(OutputLimit.MaxChars)}. " +
            (held.Partial
                ? $"Too large to hold whole: the first {N(pageable)} rows can be paged, the remaining {N(total - pageable)} cannot."
                : "No rows were dropped — they are held for paging."));

        if (held.Fields.Count > 0)
        {
            var listed = string.Join(", ", held.Fields.Take(MaxListedFields));
            var overflow = held.Fields.Count - MaxListedFields;

            if (listed.Length > MaxFieldChars)
            {
                listed = listed[..MaxFieldChars];
                overflow = held.Fields.Count - listed.Count(c => c == ',') - 1;
            }

            sb.AppendLine($"Fields: {listed}{(overflow > 0 ? $", and {N(overflow)} more" : string.Empty)}");
        }

        if (oversized)
        {
            sb.AppendLine(
                $"Row {N(offset + 1)} is {N(held.Rows[offset].Length)} characters on its own, wider than a page, so it is " +
                "not shown: returning part of it would be the truncated, unparseable output this summary exists to avoid.");
            sb.AppendLine();
            sb.AppendLine("[]");
            sb.AppendLine();
            sb.AppendLine(
                "To read it, select fewer fields (Select-Object) so the row fits, or raise PNP_MCP_MAX_OUTPUT_CHARS.");
        }
        else
        {
            sb.AppendLine($"Rows {N(offset + 1)}-{N(end)} of {N(total)}:");
            sb.AppendLine();
            sb.AppendLine("[" + string.Join(",", held.Rows.Skip(offset).Take(taken)) + "]");
        }

        sb.AppendLine();

        if (end < pageable)
        {
            sb.AppendLine(
                $"MORE: {N(pageable - end)} of the held rows remain. Call 'pnp_get_result_page' with cursor '{held.Cursor}' and offset {end}.");
        }
        else if (held.Partial)
        {
            // Held rows ended, but the result set did not.
            sb.AppendLine(
                $"END OF HELD ROWS: rows {N(pageable + 1)}-{N(total)} were never held, so no cursor reaches them. " +
                "Re-run with a narrower query — fewer fields, a filter, or -PageSize — to see the rest.");
        }
        else if (offset > 0)
        {
            sb.AppendLine($"END: this is the last page. Cursor '{held.Cursor}', offset 0 returns to the first.");
        }
        else
        {
            sb.AppendLine($"COMPLETE: every row is above. Cursor '{held.Cursor}'.");
        }

        sb.AppendLine(
            $"The result set is held in session '{sessionId}' and is replaced by the next command that runs there, " +
            "so page through it before running anything else. Re-running the command is the only way to get fresher rows.");

        return sb.ToString();
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
