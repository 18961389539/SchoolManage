using System.IO.Compression;

namespace School.Training;

public sealed record BackupInspectionReport(
    bool Ok,
    string Message,
    int? SchemaVersion,
    string KeyProtection,
    bool Portable,
    int CredentialCount,
    IReadOnlyList<string> Warnings);

public static class BackupInspection
{
    public static async Task<BackupInspectionReport> InspectZipAsync(string zipPath, KeyProtectionOptions protection, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "school-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Extract(zipPath, temp);
            return await DatabaseSafety.InspectBackupAsync(Path.Combine(temp, "school.db"), Path.Combine(temp, "keys"), protection, ct);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void Extract(string zipPath, string destination)
    {
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new InvalidDataException("备份包不是有效的 ZIP 文件"); }
        using (archive)
        {
            if (archive.Entries.Count > BackupService.MaxRestoreEntries) throw new InvalidDataException("备份条目过多");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declared = 0;
            foreach (var entry in archive.Entries)
            {
                var name = Normalize(entry.FullName) ?? throw new InvalidDataException("备份包含不安全的路径");
                if (!Allowed(name)) throw new InvalidDataException("备份包含意外文件：" + name);
                if (!seen.Add(name.TrimEnd('/'))) throw new InvalidDataException("备份包含重复条目");
                if (entry.Length < 0 || entry.Length > BackupService.MaxRestoreEntryBytes || declared > BackupService.MaxRestoreUncompressedBytes - entry.Length)
                    throw new InvalidDataException("备份超过可检查的大小上限");
                declared += entry.Length;
                var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixMode == 0xA000) throw new InvalidDataException("备份不能包含符号链接");
            }
            if (!seen.Contains("school.db")) throw new InvalidDataException("备份缺少数据库文件");
            foreach (var entry in archive.Entries)
            {
                var name = Normalize(entry.FullName)!;
                if (name.EndsWith('/') || !(name == "school.db" || name == "manifest.json" || name == ".schooltraining-data.json" || name.StartsWith("keys/")))
                    continue;
                var target = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
                var full = Path.GetFullPath(target);
                var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份包含不安全的路径");
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var input = entry.Open();
                using var output = File.Create(full);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copied += read;
                    if (copied > entry.Length || copied > BackupService.MaxRestoreEntryBytes) throw new InvalidDataException("备份条目超过声明的大小");
                    output.Write(buffer, 0, read);
                }
            }
        }
    }

    static string? Normalize(string raw)
    {
        var name = raw.Replace('\\', '/');
        if (name.Length == 0 || name.Contains(':') || name.StartsWith('/')) return null;
        var segments = name.Split('/');
        if (segments.Any(segment => segment is ".." or ".")) return null;
        return name;
    }

    static bool Allowed(string name) => name is "school.db" or "manifest.json" or ".schooltraining-data.json"
        || name.StartsWith("keys/", StringComparison.Ordinal)
        || name.StartsWith("course-files/", StringComparison.Ordinal)
        || name.StartsWith("case-evidence/", StringComparison.Ordinal);
}
