using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

namespace Nova;

internal sealed class SheetsClient
{
    private readonly SheetsService _service;

    public SheetsClient(UserCredential credential)
    {
        _service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Nova",
        });
    }

    // A1 notation range, e.g. "Sheet1!A1:D10" - omitting it reads the whole
    // first sheet (looked up by name first, since Values.Get needs an
    // explicit range/sheet name, unlike Docs.Get which always returns the
    // whole document).
    public async Task<string> ReadAsync(string spreadsheetId, string? range, CancellationToken cancellationToken)
    {
        string effectiveRange = range ?? await GetFirstSheetTitleAsync(spreadsheetId, cancellationToken);
        ValueRange result = await _service.Spreadsheets.Values.Get(spreadsheetId, effectiveRange).ExecuteAsync(cancellationToken);
        return FormatRows(result.Values, $"\"{effectiveRange}\" is empty.");
    }

    // Create only sets the title, same two-step shape as DocsClient.CreateAsync -
    // initial_rows (if given) is a separate Values.Update right after, written
    // starting at A1 of the new spreadsheet's first (default) sheet.
    public async Task<string> CreateAsync(string title, IList<IList<object>>? initialRows, CancellationToken cancellationToken)
    {
        Spreadsheet created = await _service.Spreadsheets.Create(new Spreadsheet
        {
            Properties = new SpreadsheetProperties { Title = title },
        }).ExecuteAsync(cancellationToken);

        if (initialRows is { Count: > 0 })
        {
            await WriteRangeAsync(created.SpreadsheetId, "A1", initialRows, cancellationToken);
        }

        return $"Created \"{title}\" (id: {created.SpreadsheetId}).";
    }

    // Appends after the last row with data in the given range (or the whole
    // first sheet if omitted) - Sheets.Values.Append finds the actual
    // insertion point itself, no need to know how many rows already exist.
    public async Task<string> AppendRowsAsync(string spreadsheetId, string? range, IList<IList<object>> rows, CancellationToken cancellationToken)
    {
        string effectiveRange = range ?? await GetFirstSheetTitleAsync(spreadsheetId, cancellationToken);
        var body = new ValueRange { Values = rows };
        SpreadsheetsResource.ValuesResource.AppendRequest request = _service.Spreadsheets.Values.Append(body, spreadsheetId, effectiveRange);
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        await request.ExecuteAsync(cancellationToken);
        return $"Appended {rows.Count} row(s) to {effectiveRange}.";
    }

    // Writes/overwrites a specific range - the Sheets analog of
    // replace_in_doc: cells are addressed positionally (A1 notation), not
    // by matching existing text, since that's how a spreadsheet's own UI
    // addresses them too.
    public async Task<string> UpdateRangeAsync(string spreadsheetId, string range, IList<IList<object>> values, CancellationToken cancellationToken)
    {
        await WriteRangeAsync(spreadsheetId, range, values, cancellationToken);
        return $"Wrote {values.Count} row(s) to {range}.";
    }

    private async Task WriteRangeAsync(string spreadsheetId, string range, IList<IList<object>> values, CancellationToken cancellationToken)
    {
        var body = new ValueRange { Values = values };
        SpreadsheetsResource.ValuesResource.UpdateRequest request = _service.Spreadsheets.Values.Update(body, spreadsheetId, range);
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
        await request.ExecuteAsync(cancellationToken);
    }

    private async Task<string> GetFirstSheetTitleAsync(string spreadsheetId, CancellationToken cancellationToken)
    {
        Spreadsheet spreadsheet = await _service.Spreadsheets.Get(spreadsheetId).ExecuteAsync(cancellationToken);
        string title = spreadsheet.Sheets?.FirstOrDefault()?.Properties?.Title ?? "Sheet1";
        return QuoteSheetName(title);
    }

    // A1 notation requires a sheet name to be single-quoted whenever it
    // contains a space or other special character (e.g. "Raw Data" ->
    // 'Raw Data'!A1:Z1000) - an unquoted name with a space in it is a
    // parse error the Sheets API rejects outright. Quoting is always valid
    // even when not strictly required, so this quotes unconditionally
    // rather than trying to detect which names actually need it; an
    // embedded single quote in the name itself is escaped by doubling it,
    // the same way A1 notation itself escapes one.
    private static string QuoteSheetName(string sheetName) => $"'{sheetName.Replace("'", "''")}'";

    private static string FormatRows(IList<IList<object>>? rows, string emptyMessage)
    {
        if (rows is null || rows.Count == 0)
        {
            return emptyMessage;
        }

        var sb = new StringBuilder();
        foreach (IList<object> row in rows)
        {
            sb.AppendLine(string.Join("\t", row.Select(cell => cell?.ToString() ?? "")));
        }

        return sb.ToString();
    }
}
