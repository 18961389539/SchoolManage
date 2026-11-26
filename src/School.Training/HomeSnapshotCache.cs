using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace School.Training;

/// <summary>
/// Repeating the admin or teacher home within ten seconds, with no new learning or roster
/// changes, reuses the previous payload.
/// </summary>
public sealed class HomeSnapshotCache
{
    readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 64 });

    public bool TryGet(string key, out object? value) => cache.TryGetValue(key, out value);

    public void Set(string key, object value)
        => cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10), Size = 1 });

    public static async Task<string> VersionAsync(SchoolDbContext db, string scope, CancellationToken ct)
    {
        var stat = await db.ClassroomStudentStats.MaxAsync(item => (DateTime?)item.UpdatedAt, ct);
        var frozen = await db.ClassroomProgressFreezes.MaxAsync(item => (DateTime?)item.FrozenAt, ct);
        var audit = await db.Audits.MaxAsync(item => (DateTime?)item.At, ct);
        var classrooms = await db.Classrooms.CountAsync(ct);
        var enrollments = await db.ClassroomStudents.CountAsync(ct);
        var students = await db.People.CountAsync(item => item.Kind == "student", ct);
        var classes = await db.Classes.CountAsync(ct);
        var resources = await db.CourseResources.CountAsync(ct);
        return string.Join('|', scope, Ticks(stat), Ticks(frozen), Ticks(audit), classrooms, enrollments, students, classes, resources);
    }

    static long Ticks(DateTime? value) => value?.Ticks ?? 0;
}
