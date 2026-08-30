using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>
/// Builds a tool result carrying both prose and structured content, inside one output budget.
///
/// A tool that returns both puts both into the caller's context, so <see cref="OutputLimit"/>'s cap has
/// to bound their sum: capping each half separately would let one call deliver twice the configured
/// limit of what is largely the same content. Shared rather than written per tool, because every tool
/// converted to structured output needs exactly this and would otherwise re-derive it.
/// </summary>
internal static class StructuredResult
{
    /// <summary>
    /// A result whose size does not depend on how many items it carries. There is nothing to shrink, so
    /// a payload that still overruns the cap is dropped rather than sent: the cap is a hard bound, and
    /// the prose half already carries the same facts. Callers with a list should use
    /// <see cref="FitToCap"/>, which keeps as much as fits instead.
    /// </summary>
    public static CallToolResult From<T>(T value, JsonTypeInfo<T> typeInfo, Func<T, string> render)
    {
        var text = render(value);
        var structured = JsonSerializer.SerializeToElement(value, typeInfo);

        if (text.Length + structured.GetRawText().Length > OutputLimit.MaxChars)
        {
            // Said out loud. The tool advertises an output schema, so a client that trusted it would
            // otherwise be unable to tell "there was no data" from "the data was withheld".
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = text +
                            "\n\n[structured output omitted: it did not fit alongside this text within " +
                            $"{OutputLimit.MaxChars} characters. Everything it carried is stated above.]",
                    },
                ],
            };
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = structured,
        };
    }

    /// <summary>
    /// The largest prefix of <paramref name="items"/> whose rendered text and serialized JSON together
    /// fit the output cap.
    /// </summary>
    /// <param name="items">Candidates, most important first; only a prefix is ever kept.</param>
    /// <param name="project">Builds the result from a prefix. <c>lean</c> asks for a smaller per-item shape.</param>
    /// <remarks>
    /// Bisected rather than repeatedly halved: halving stops at the first power of two under the cap,
    /// which returned a single item at budgets with room for several. Never shrinks below one item —
    /// reporting zero would read as "nothing matched", a different and false answer — so a single item
    /// too large even at its leanest is returned anyway rather than looped over forever.
    /// </remarks>
    public static CallToolResult FitToCap<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<IReadOnlyList<TItem>, bool, TResult> project,
        JsonTypeInfo<TResult> typeInfo,
        Func<TResult, string> render)
    {
        var whole = Measure(items.Count, lean: false);
        if (whole.Total <= OutputLimit.MaxChars)
        {
            return whole.Result;
        }

        var low = 1;
        var high = items.Count - 1;
        var best = 0;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);

            if (Measure(middle, lean: false).Total <= OutputLimit.MaxChars)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best > 0
            ? Measure(best, lean: false).Result
            : Measure(Math.Min(1, items.Count), lean: true).Result;

        (CallToolResult Result, int Total) Measure(int take, bool lean)
        {
            var value = project([.. items.Take(take)], lean);
            var structured = JsonSerializer.SerializeToElement(value, typeInfo);
            var text = render(value);

            return (
                new CallToolResult
                {
                    Content = [new TextContentBlock { Text = text }],
                    StructuredContent = structured,
                },
                // GetRawText transcodes the whole payload, so it is measured once per attempt.
                text.Length + structured.GetRawText().Length);
        }
    }

    /// <summary>A plain text result, for the error paths that carry nothing to structure.</summary>
    public static CallToolResult Text(string text, bool isError = false) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };
}
