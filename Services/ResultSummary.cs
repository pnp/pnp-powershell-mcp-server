using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>A JSON result set held for paging, keyed by an opaque cursor.</summary>
internal sealed class HeldResultSet
{
    public required string Cursor { get; init; }
    public required IReadOnlyList<string> Rows { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
    public required int RawLength { get; init; }
}

/// <summary>Turns an oversized JSON array into a summary plus a page, instead of a truncated fragment.</summary>
// Truncation spends the tokens and returns unparseable JSON; summarising keeps the bound and the answer.
internal static class ResultSummary
{
    // Reserved for the header, the field list and the continuation footer before any row is measured.
    private const int Overhead = 2_000;

    // The list repeats on every page, so naming fifty-odd fields stops being useful past the first few.
    private const int MaxListedFields = 25;

    // Bounds the field line in characters as well as in count, so Overhead cannot be overrun by long names.
    private const int MaxFieldChars = 600;

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

            foreach (var element in document.RootElement.EnumerateArray())
            {
                rows.Add(element.GetRawText());

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
        var total = held.Rows.Count;
        offset = Math.Clamp(offset, 0, Math.Max(total - 1, 0));

        var budget = Math.Max(OutputLimit.MaxChars - Overhead, 500);
        var taken = 0;
        var used = 0;

        while (offset + taken < total && used + held.Rows[offset + taken].Length + 1 <= budget)
        {
            used += held.Rows[offset + taken].Length + 1;
            taken++;
        }

        // A row wider than a whole page cannot be emitted without the output cap cutting it mid-token,
        // which is the truncated, unparseable answer this class exists to avoid. Skip it and say so.
        var oversized = taken == 0 && offset < total;
        var end = offset + (oversized ? 1 : taken);

        var sb = new StringBuilder();
        sb.AppendLine(
            $"Result set: {N(total)} rows, summarised because the full output is {N(held.RawLength)} characters " +
            $"and the cap is {N(OutputLimit.MaxChars)}. No rows were dropped — they are held for paging.");

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

        if (end < total)
        {
            sb.AppendLine(
                $"MORE: {N(total - end)} rows remain. Call 'pnp_get_result_page' with cursor '{held.Cursor}' and offset {end}.");
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
