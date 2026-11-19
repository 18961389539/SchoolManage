using System.Text;

namespace School.Training;

/// <summary>Raised when a parsed sheet cannot be turned into import rows.</summary>
public sealed class ImportSheetException(string message) : Exception(message);

/// <summary>
/// CSV parsing plus header alias mapping for the people import flow. The endpoint
/// only deals with HTTP concerns; everything here is deterministic and unit tested.
/// </summary>
public static class ImportCsv
{
    /// <summary>Header row plus at most 10000 data rows.</summary>
    public const int MaxSheetRows = 10001;

    public static readonly IReadOnlyDictionary<string, string> HeaderAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["姓名"] = "name",
            ["账号"] = "userName",
            ["学号"] = "number",
            ["工号"] = "number",
            ["学号/工号"] = "number",
            ["班级"] = "className",
            ["性别"] = "gender",
            ["手机号"] = "phone",
            ["手机号码"] = "phone",
            ["电子邮箱"] = "email",
            ["邮箱"] = "email",
            ["name"] = "name",
            ["userName"] = "userName",
            ["number"] = "number",
            ["className"] = "className",
            ["gender"] = "gender",
            ["phone"] = "phone",
            ["email"] = "email"
        };

    public static readonly string[] RequiredHeaders = ["name", "userName", "number"];

    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { row.Add(value.ToString()); value.Clear(); }
            else if ((c == '\r' || c == '\n') && !quoted)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(value.ToString());
                value.Clear();
                rows.Add(row);
                row = [];
                if (rows.Count > MaxSheetRows) throw new FormatException("Too many rows");
            }
            else value.Append(c);
        }
        if (quoted) throw new FormatException("Unclosed quote");
        if (value.Length > 0 || row.Count > 0) { row.Add(value.ToString()); rows.Add(row); }
        return rows;
    }

    public static List<string> MapHeaders(IReadOnlyList<string> headerRow)
        => headerRow.Select(header => HeaderAliases.GetValueOrDefault(header.Trim('\uFEFF').Trim(), "")).ToList();

    /// <summary>
    /// Validates the header row and converts data rows into <see cref="ImportRow"/> values.
    /// Throws <see cref="ImportSheetException"/> with a user-facing message when the sheet is unusable.
    /// </summary>
    public static List<ImportRow> BuildRows(IReadOnlyList<List<string>> sheet)
    {
        if (sheet.Count == 0) throw new ImportSheetException("文件没有可导入的数据");
        var headers = MapHeaders(sheet[0]);
        if (RequiredHeaders.Any(header => !headers.Contains(header)))
            throw new ImportSheetException("缺少姓名、账号、学号/工号表头，请下载导入模板");
        if (headers.Where(header => header.Length > 0).GroupBy(header => header).Any(group => group.Count() > 1))
            throw new ImportSheetException("表头包含重复列");

        var rows = new List<ImportRow>();
        for (var index = 1; index < sheet.Count; index++)
        {
            if (sheet[index].All(string.IsNullOrWhiteSpace)) continue;
            var cells = sheet[index];
            string Get(string header)
            {
                var position = headers.IndexOf(header);
                return position >= 0 && position < cells.Count ? cells[position].Trim() : "";
            }
            rows.Add(new ImportRow
            {
                RowNumber = index + 1,
                Data = new ImportData(Get("name"), Get("userName").ToLowerInvariant(), Get("number"), Get("className"), Get("gender"), Get("phone"), Get("email"))
            });
        }
        return rows.Count == 0 ? throw new ImportSheetException("文件没有可导入的数据") : rows;
    }
}
