using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

/// <summary>
/// Streams the audit table as CSV so a large history never has to be materialized in
/// memory. Cells that Excel would evaluate as a formula are prefixed with a single quote.
/// </summary>
public static class AuditExport
{
    static readonly TimeZoneInfo SchoolZone = FindSchoolZone();

    static TimeZoneInfo FindSchoolZone()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        return TimeZoneInfo.Utc;
    }

    public static async Task<IResult> CsvAsync(HttpRequest request, SchoolDbContext db, HttpContext ctx)
    {
        var search = request.Query["search"].ToString().Trim();
        var query = db.Audits.AsNoTracking();
        if (search.Length > 0)
            query = query.Where(x => x.UserName.Contains(search) || x.Action.Contains(search) || x.Target.Contains(search));

        await db.Audits.AddAsync(new AuditEntry
        {
            UserName = ctx.User.Identity?.Name ?? "system",
            Action = "导出操作日志",
            Target = search.Length > 0 ? search : "全部"
        }, ctx.RequestAborted);
        await db.SaveChangesAsync(ctx.RequestAborted);

        var rows = query.OrderBy(x => x.At).ThenBy(x => x.Id).AsAsyncEnumerable();
        var fileName = $"audits-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        return Results.Stream(async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 16 * 1024, leaveOpen: true);
            await writer.WriteLineAsync("时间（北京时间）,账号,操作,对象,详情");
            await foreach (var row in rows.WithCancellation(ctx.RequestAborted))
            {
                await writer.WriteLineAsync(string.Join(',',
                    Cell(BeijingTime(row.At).ToString("yyyy-MM-dd HH:mm:ss")),
                    Cell(row.UserName), Cell(row.Action), Cell(row.Target), Cell(row.Detail)));
            }
            await writer.FlushAsync(ctx.RequestAborted);
        }, "text/csv; charset=utf-8", fileName);
    }

    /// <summary>
    /// SQLite reads DateTime back as Unspecified. Audit timestamps are stored as UTC, so a
    /// naive value must not pass through the server's local time zone on the way to Beijing.
    /// </summary>
    internal static DateTime BeijingTime(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, SchoolZone);
    }

    internal static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
            text = "\"" + text.Replace("\"", "\"\"") + "\"";
        return text;
    }
}
