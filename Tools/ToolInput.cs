using System.Collections.Generic;
using System.Text.Json;

namespace Nova;

internal static class ToolInput
{
    public static string? GetString(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    public static bool? GetBool(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var element) && (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
            ? element.GetBoolean()
            : null;

    public static int? GetInt(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.Number ? (int)element.GetDouble() : null;

    // Sheets' own API takes cell values as an array of rows, each row an
    // array of cell values - used by create_sheet/append_sheet_rows/
    // update_sheet_range (see SheetsClient). Numbers/booleans are kept as
    // their real .NET type rather than stringified, so Sheets' own
    // USER_ENTERED value-input parsing treats "12" written as a number
    // the same way typing it into a cell would, not as literal text.
    public static IList<IList<object>>? GetRows(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (!input.TryGetValue(key, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var rows = new List<IList<object>>();
        foreach (JsonElement rowElement in element.EnumerateArray())
        {
            var row = new List<object>();
            foreach (JsonElement cell in rowElement.EnumerateArray())
            {
                row.Add(cell.ValueKind switch
                {
                    JsonValueKind.String => cell.GetString()!,
                    JsonValueKind.Number => cell.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => cell.GetRawText(),
                });
            }

            rows.Add(row);
        }

        return rows;
    }
}
