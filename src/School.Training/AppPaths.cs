namespace School.Training;

public sealed class AppPaths
{
    public string Data { get; }
    public string Database => Path.Combine(Data, "school.db");
    public string Backups { get; }
    public string Keys => Path.Combine(Data, "keys");
    public string Logs => Path.Combine(Data, "logs");
    public string CourseFiles => Path.Combine(Data, "course-files");
    public string CaseEvidence => Path.Combine(Data, "case-evidence");
    public AppPaths(string data, string? backups = null)
    {
        Data = Path.GetFullPath(data);
        if (Data.TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(Data)?.TrimEnd(Path.DirectorySeparatorChar)) throw new InvalidOperationException("数据目录必须是专用子目录");
        if (!string.IsNullOrWhiteSpace(backups) && !Path.IsPathRooted(backups)) throw new InvalidOperationException("备份目录必须是绝对路径");
        Backups = string.IsNullOrWhiteSpace(backups) ? Path.Combine(Data, "backups") : Path.GetFullPath(backups);
        if (Backups.TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(Backups)?.TrimEnd(Path.DirectorySeparatorChar)) throw new InvalidOperationException("备份目录必须是专用子目录");
        if (string.Equals(Backups.TrimEnd(Path.DirectorySeparatorChar), Data.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("备份目录不能与数据目录相同");
        Directory.CreateDirectory(Data);
        var marker = Path.Combine(Data, ".schooltraining-data.json");
        if (File.Exists(marker))
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker));
            if (!json.RootElement.TryGetProperty("product", out var product) || product.GetString() != "SchoolTraining") throw new InvalidOperationException("数据目录的产品标记不匹配");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(Data).Any()) throw new InvalidOperationException("数据目录已有未知文件，请选择专用空目录；不要覆盖其他文件");
            File.WriteAllText(marker, "{\"product\":\"SchoolTraining\",\"schemaVersion\":1}");
        }
        foreach (var path in new[] { Data, Backups, Keys, Logs, CourseFiles, CaseEvidence }) Directory.CreateDirectory(path);
    }
}

public static class Audit
{
    public static void Add(SchoolDbContext db, HttpContext ctx, string action, string target, string detail = "")
        => db.Audits.Add(new AuditEntry { UserName = ctx.User.Identity?.Name ?? "system", Action = action, Target = target, Detail = detail });
}
