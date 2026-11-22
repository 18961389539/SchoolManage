using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;

namespace School.Training;

/// <summary>Shared multipart xlsx/csv sheet reader for question imports.</summary>
public static class ImportFiles
{
    public static async Task<(IFormCollection? Form, List<List<string>>? Sheet, string? Error)> ReadSheetAsync(HttpContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.HasFormContentType) return (null, null, "请选择 Excel 或 CSV 文件");
        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0 || file.Length > Limits.MaxUploadBytes)
            return (form, null, $"文件为空或超过 {Limits.MaxUploadMegabytes}MB");
        List<List<string>> sheet;
        try
        {
            await using var stream = file.OpenReadStream();
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext == ".xlsx")
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
                {
                    if (archive.Entries.Count > 2000 || archive.Entries.Sum(entry => entry.Length) > 64 * 1024 * 1024)
                        return (form, null, "Excel 解压后内容超过 64MB 或结构过于复杂");
                }
                stream.Position = 0;
                using var book = new XLWorkbook(stream);
                var worksheet = book.Worksheets.FirstOrDefault();
                if (worksheet == null) return (form, null, "工作簿没有工作表");
                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
                var lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
                if (lastRow == 0 || lastColumn == 0) return (form, null, "工作表为空");
                if (lastRow > ExamImport.MaxSheetRows || lastColumn > 30)
                    return (form, null, "数据必须位于前 10001 行、30 列以内（含表头）");
                sheet = Enumerable.Range(1, lastRow)
                    .Select(row => Enumerable.Range(1, lastColumn).Select(column => worksheet.Cell(row, column).GetFormattedString().Trim()).ToList())
                    .ToList();
            }
            else if (ext == ".csv")
            {
                using var streamReader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                sheet = ImportCsv.Parse(await streamReader.ReadToEndAsync(ct));
            }
            else return (form, null, "仅支持 .xlsx 和 UTF-8 编码的 .csv 文件");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (form, null, "无法读取文件，请检查文件格式和 UTF-8 编码");
        }
        if (sheet.Count is < 2 or > ExamImport.MaxSheetRows) return (form, null, "需要表头和至少一行数据，每次最多 10000 行");
        return (form, sheet, null);
    }
}
