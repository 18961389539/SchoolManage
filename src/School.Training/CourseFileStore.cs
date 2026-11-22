using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed record StoredCourseFile(string ResourceId, string StoredName);

/// <summary>Physical attachment storage under the application data directory.</summary>
public static class CourseFileStore
{
    public static string DirectoryFor(AppPaths paths, string resourceId) => Path.Combine(paths.CourseFiles, resourceId);

    public static string PathFor(AppPaths paths, string resourceId, string storedName) => Path.Combine(DirectoryFor(paths, resourceId), storedName);

    public static async Task<List<StoredCourseFile>> ListAsync(SchoolDbContext db, IEnumerable<string> resourceIds, CancellationToken ct)
    {
        var ids = resourceIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await db.CourseResourceFiles.AsNoTracking().Where(item => ids.Contains(item.ResourceId))
            .Select(item => new { item.ResourceId, item.StoredName }).ToListAsync(ct);
        return rows.Select(row => new StoredCourseFile(row.ResourceId, row.StoredName)).ToList();
    }

    public static void Delete(AppPaths paths, IEnumerable<StoredCourseFile> files)
    {
        foreach (var group in files.GroupBy(file => file.ResourceId))
        {
            var directory = DirectoryFor(paths, group.Key);
            foreach (var file in group)
            {
                try { File.Delete(PathFor(paths, group.Key, file.StoredName)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
