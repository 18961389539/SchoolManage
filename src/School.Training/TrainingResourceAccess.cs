using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class TrainingResourceAccess
{
    public static IQueryable<TrainingNode> AssignedNodes(SchoolDbContext db, string userId)
        => db.Nodes.AsNoTracking().Where(node => node.Provider == ResourceProviders.Simulated
            && db.NodeAccountAssignments.Any(assignment => assignment.NodeId == node.Id && assignment.UserId == userId));

    // A single SQL predicate supports checks and conditional writes without a permission/write race.
    public static IQueryable<TrainingDevice> AccessibleDevices(SchoolDbContext db, string userId, string securityStamp)
    {
        var assignedNodes = AssignedNodes(db, userId).Where(node => node.Enabled);
        var granted = ClassroomCabinetIds(db, userId);
        var reserved = OpenCabinetAssignments(db).Select(item => item.CabinetId);
        return db.Devices.Where(device => db.Users.Any(user => user.Id == userId
            && user.SecurityStamp == securityStamp && !user.MustChangePassword
            && (user.Role == "Admin" || ((user.Role == "Teacher" || user.Role == "Student")
                && device.ConnectionType == ResourceProviders.Simulated
                && ((granted.Contains(device.CabinetId) && (device.NodeId == null || db.Nodes.Any(node =>
                    node.Id == device.NodeId && node.Enabled && node.Provider == ResourceProviders.Simulated)))
                    || (!reserved.Contains(device.CabinetId) && assignedNodes.Any(node => node.Id == device.NodeId)))))));
    }

    static IQueryable<ClassroomCabinetAssignment> OpenCabinetAssignments(SchoolDbContext db)
        => from assignment in db.ClassroomCabinetAssignments
           join room in TeachingLogic.OpenClassrooms(db, DateTime.UtcNow) on assignment.ClassroomId equals room.Id
           join cabinet in db.Cabinets on assignment.CabinetId equals cabinet.Id
           where cabinet.IsSimulated
           select assignment;

    public static IQueryable<string> ClassroomCabinetIds(SchoolDbContext db, string userId, string? classroomId = null, string? resourceId = null)
        => from assignment in OpenCabinetAssignments(db)
           join room in db.Classrooms on assignment.ClassroomId equals room.Id
           where (classroomId == null || assignment.ClassroomId == classroomId)
               && (resourceId == null || assignment.ResourceId == resourceId)
               && (room.TeacherUserId == userId
                   || (db.ClassroomGroupMembers.Any(member => member.GroupId == assignment.GroupId && member.StudentUserId == userId)
                       && db.ClassroomStudents.Any(student => student.ClassroomId == room.Id && student.StudentUserId == userId)))
           select assignment.CabinetId;

    public static Task<bool> CanAccessDeviceAsync(SchoolDbContext db, ClaimsPrincipal principal, TrainingDevice device, CancellationToken ct)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = principal.FindFirstValue("stamp");
        return userId is null || stamp is null
            ? Task.FromResult(false)
            : CanAccessDeviceAsync(db, userId, stamp, device.Id, ct);
    }

    public static Task<bool> CanAccessDeviceAsync(SchoolDbContext db, string userId, string securityStamp, string deviceId, CancellationToken ct)
        => AccessibleDevices(db, userId, securityStamp).AnyAsync(device => device.Id == deviceId, ct);
}
