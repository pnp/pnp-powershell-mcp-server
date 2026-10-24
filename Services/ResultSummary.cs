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

    /// <summary>Whatever printed around the array, such as warnings; clamped, with its full length kept.</summary>
    public string? Notes { get; init; }

    public int NotesLength { get; init; }

    /// <summary>True when the result was too large to hold whole and only a prefix is pageable.</summary>
    public bool Partial => TotalRows > Rows.Count;
}

/// <summary>Summarises an oversized JSON array into pages instead of truncating it.</summary>
internal static class ResultSummary
{
    // The list repeats on every page, so naming fifty-odd fields stops being useful past the first few.
    private const int MaxListedFields = 25;

    // Bounds the field line in characters as well as in count, so long names cannot crowd out the rows.
    private const int MaxFieldChars = 600;

    /// <summary>Ceiling on what a session pins in memory. Rows past it are counted, not kept.</summary>
    private const int MaxHeldChars = 8_000_000;

    /// <summary>Row ceiling too: many tiny rows cost more than their characters.</summary>
    private const int MaxHeldRows = 100_000;

    private const int MaxSessionIdChars = 40;

    // Clamped on capture; a page adds them only when they fit.
    private const int MaxNoteChars = 400;

    /// <summary>Captures a JSON array of two or more elements; null for anything else.</summary>
    public static HeldResultSet? TryCapture(string? output)
    {
        var text = output?.TrimStart() ?? string.Empty;

        // Warnings print ahead of the array and stderr lands after it, so the array is found by line.
        var start = text.StartsWith('[') ? 0 : text.IndexOf("\n[", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text, start, text.Length - start);
            var reader = new Utf8JsonReader(bytes);
            using var document = JsonDocument.ParseValue(ref reader);
            var notes = $"{text[..start]}\n{Encoding.UTF8.GetString(bytes.AsSpan((int)reader.BytesConsumed))}".Trim();

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
                    Notes = notes.Length > 0 ? OutputLimit.Clamp(notes, MaxNoteChars) : null,
                    NotesLength = notes.Length,
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which held rows one page covers. Shared so the rendered page and the offsets reported alongside it
    /// cannot disagree. <c>Oversized</c> means the row at <c>Start</c> is wider than a whole page.
    /// </summary>
    public static (int Start, int End, int Pageable, bool Oversized) Paging(HeldResultSet held, int offset)
    {
        // Paging walks held rows; every reported figure is the true total.
        var pageable = held.Rows.Count;
        var start = Math.Clamp(offset, 0, Math.Max(pageable - 1, 0));

        // Whatever this set's own page frame leaves, less the notice a text-only page carries.
        var budget = OutputLimit.MaxChars - StructuredResult.OmittedNoticeLength - Frame(held);
        var taken = 0;
        var used = 0;

        while (start + taken < pageable && used + held.Rows[start + taken].Length + 1 <= budget)
        {
            used += held.Rows[start + taken].Length + 1;
            taken++;
        }

        // A row wider than a page would be cut mid-token, so skip it.
        var oversized = taken == 0 && start < pageable;

        return (start, start + (oversized ? 1 : taken), pageable, oversized);
    }

    /// <summary>What any page of this set spends besides its rows, with every variable part at its widest.</summary>
    private static int Frame(HeldResultSet held)
    {
        var line = Environment.NewLine.Length;
        var pageable = held.Rows.Count;
        string[] footers =
        [
            MoreLine(pageable, held.Cursor, pageable),
            HeldEndLine(pageable + 1, held.TotalRows),
            EndLine(held.Cursor),
            CompleteLine(held.Cursor),
        ];

        return HeaderLine(held).Length + line
            + (FieldsLine(held) is { } fields ? fields.Length + line : 0)
            + RowsLine(held.TotalRows, held.TotalRows, held.TotalRows).Length + (2 * line)
            + "[]".Length + (2 * line)
            + footers.Max(f => f.Length) + line
            + SessionLine(new string('s', MaxSessionIdChars + 1)).Length + line;
    }

    private static string HeaderLine(HeldResultSet held) =>
        $"Result set: {N(held.TotalRows)} rows, summarised because the full output is {N(held.RawLength)} characters " +
        $"and the cap is {N(OutputLimit.MaxChars)}. " +
        (held.Partial
            ? $"Too large to hold whole: the first {N(held.Rows.Count)} rows can be paged, the remaining {N(held.TotalRows - held.Rows.Count)} cannot."
            : "No rows were dropped — they are held for paging.");

    private static string? FieldsLine(HeldResultSet held)
    {
        if (held.Fields.Count == 0)
        {
            return null;
        }

        var listed = string.Join(", ", held.Fields.Take(MaxListedFields));
        var overflow = held.Fields.Count - MaxListedFields;

        if (listed.Length > MaxFieldChars)
        {
            listed = listed[..MaxFieldChars];
            overflow = held.Fields.Count - listed.Count(c => c == ',') - 1;
        }

        return $"Fields: {listed}{(overflow > 0 ? $", and {N(overflow)} more" : string.Empty)}";
    }

    private static string RowsLine(int first, int last, int total) => $"Rows {N(first)}-{N(last)} of {N(total)}:";

    private static string MoreLine(int remaining, string cursor, int next) =>
        $"MORE: {N(remaining)} of the held rows remain. Call 'pnp_get_result_page' with cursor '{cursor}' and offset {next}.";

    // Held rows ended, but the result set did not.
    private static string HeldEndLine(int firstUnheld, int total) =>
        $"END OF HELD ROWS: rows {N(firstUnheld)}-{N(total)} were never held, so no cursor reaches them. " +
        "Re-run with a narrower query — fewer fields, a filter, or -PageSize — to see the rest.";

    private static string EndLine(string cursor) => $"END: this is the last page. Cursor '{cursor}', offset 0 returns to the first.";

    private static string CompleteLine(string cursor) => $"COMPLETE: every row is above. Cursor '{cursor}'.";

    private static string SessionLine(string sessionId) =>
        $"The result set is held in session '{Name(sessionId)}' and is replaced by the next command that runs there, " +
        "so page through it before running anything else. Re-running the command is the only way to get fresher rows.";

    /// <summary>Renders one page: what the whole result set is, then as many rows from <paramref name="offset"/> as fit.</summary>
    public static string Render(HeldResultSet held, int offset, string sessionId)
    {
        var (start, end, pageable, oversized) = Paging(held, offset);
        var total = held.TotalRows;
        var taken = oversized ? 0 : end - start;
        offset = start;

        var sb = new StringBuilder();
        sb.AppendLine(HeaderLine(held));

        if (FieldsLine(held) is { } fields)
        {
            sb.AppendLine(fields);
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
            sb.AppendLine(RowsLine(offset + 1, end, total));
            sb.AppendLine();
            sb.AppendLine("[" + string.Join(",", held.Rows.Skip(offset).Take(taken)) + "]");
        }

        sb.AppendLine();

        sb.AppendLine(
            end < pageable ? MoreLine(pageable - end, held.Cursor, end)
            : held.Partial ? HeldEndLine(pageable + 1, total)
            : offset > 0 ? EndLine(held.Cursor)
            : CompleteLine(held.Cursor));

        sb.AppendLine(SessionLine(sessionId));

        // Last, and only what fits: Paging reserves no room for it, and a page cut by the cap is no longer valid JSON.
        if (held.Notes is { } notes)
        {
            var room = OutputLimit.MaxChars - StructuredResult.OmittedNoticeLength;
            var full = $"Also printed: {notes}";
            var note = sb.Length + full.Length + 2 <= room
                ? full
                : $"Also printed: {N(held.NotesLength)} characters of other output, omitted to fit the output cap.";

            if (sb.Length + note.Length + 2 <= room)
            {
                sb.AppendLine(note);
            }
        }

        return sb.ToString();
    }

    /// <summary>Caps the caller-supplied session id, which could otherwise overrun the page.</summary>
    private static string Name(string sessionId)
    {
        var clean = new string([.. sessionId.Where(c => !char.IsControl(c)).Take(MaxSessionIdChars + 1)]);

        return clean.Length > MaxSessionIdChars ? clean[..MaxSessionIdChars] + "..." : clean;
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
