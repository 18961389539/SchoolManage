using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class CampAccess
{
    public static IQueryable<JobAttempt> TeacherControlScope(IQueryable<JobAttempt> source, SchoolDbContext db, string teacherUserId)
        => source.Where(attempt =>
            db.ClassroomStudents.Any(student => student.StudentUserId == attempt.UserId
                && db.Classrooms.Any(classroom => classroom.Id == student.ClassroomId && classroom.TeacherUserId == teacherUserId))
            || db.VirtualMachines.Any(vm => vm.Id == attempt.VirtualMachineId
                && db.NodeAccountAssignments.Any(assignment => assignment.NodeId == vm.NodeId && assignment.UserId == teacherUserId))
            || db.JobPositions.Any(position => position.Id == attempt.PositionId && position.StartupNodeId != null
                && db.NodeAccountAssignments.Any(assignment => assignment.NodeId == position.StartupNodeId && assignment.UserId == teacherUserId)));

    public static IQueryable<JobAttempt> TeacherControlScope(SchoolDbContext db, string teacherUserId)
        => TeacherControlScope(db.JobAttempts, db, teacherUserId);

    public static Task<bool> CanControlAsync(SchoolDbContext db, HttpContext ctx, JobAttempt attempt)
        => ctx.User.IsInRole("Admin")
            ? Task.FromResult(true)
            : TeacherControlScope(db.JobAttempts.Where(item => item.Id == attempt.Id), db, CurrentUserId(ctx)).AnyAsync();

    public static string CurrentUserId(HttpContext ctx)
        => ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
}