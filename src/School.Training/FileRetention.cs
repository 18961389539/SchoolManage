namespace School.Training;

/// <summary>
/// Bounded deletion for operational artifacts. Deletion is always restricted to a
/// single directory and a literal search pattern; failures are non-fatal.
/// </summary>
public static class FileRetention
{
    public static int Apply(string directory, string searchPattern, int maxCount, long maxTotalBytes, DateTime? olderThanUtc = null, string? protectedFile = null)
    {
        if (!Directory.Exists(directory) || maxCount < 1 || maxTotalBytes < 0) return 0;
        FileInfo[] files;
        try { files = new DirectoryInfo(directory).GetFiles(searchPattern, SearchOption.TopDirectoryOnly); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        var ordered = files.OrderByDescending(x => x.LastWriteTimeUtc).ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var deleted = 0;
        long retainedBytes = 0;
        var retainedCount = 0;
        foreach (var file in ordered)
        {
            var isProtected = protectedFile != null && string.Equals(file.FullName, Path.GetFullPath(protectedFile), StringComparison.OrdinalIgnoreCase);
            var tooOld = olderThanUtc != null && file.LastWriteTimeUtc < olderThanUtc.Value;
            var tooMany = retainedCount >= maxCount;
            var tooLarge = retainedBytes + file.Length > maxTotalBytes;
            if (!isProtected && (tooOld || tooMany || tooLarge))
            {
                try { file.Delete(); deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                continue;
            }
            retainedCount++;
            retainedBytes += file.Length;
        }
        return deleted;
    }
}
