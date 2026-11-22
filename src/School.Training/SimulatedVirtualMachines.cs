using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class SimulatedVirtualMachines
{
    public static IQueryable<ClassroomCaseSession> ActiveClassrooms(SchoolDbContext db)
    {
        var rooms = TeachingLogic.OpenClassrooms(db, DateTime.UtcNow);
        return db.ClassroomCaseSessions.Where(session => session.Status == "active" && session.VirtualMachineId != null
            && rooms.Any(room => room.Id == session.ClassroomId));
    }

    public static IQueryable<JobAttempt> ActiveCamp(SchoolDbContext db, DateTime now)
        => db.JobAttempts.Where(attempt => attempt.Status == "inProgress" && attempt.VirtualMachineId != null && attempt.Deadline > now);

    public static IQueryable<string> ReservedIds(SchoolDbContext db, DateTime now, string? classroomSessionId = null, string? campAttemptId = null)
        => ActiveClassrooms(db).Where(session => session.Id != classroomSessionId).Select(session => session.VirtualMachineId!)
            .Union(ActiveCamp(db, now).Where(attempt => attempt.Id != campAttemptId).Select(attempt => attempt.VirtualMachineId!));

    public static async Task<string> AllocateAddress(SchoolDbContext db, CancellationToken ct)
    {
        while (true)
        {
            var candidate = $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";
            if (db.VirtualMachines.Local.All(vm => vm.IpAddress != candidate)
                && !await db.VirtualMachines.AnyAsync(vm => vm.IpAddress == candidate, ct)) return candidate;
        }
    }

    public static async Task<bool> CanBoot(SchoolDbContext db, TrainingNode node, TrainingVirtualMachine vm, CancellationToken ct)
    {
        var (cpu, memory) = await FreeCapacity(db, node, vm.Id, ct);
        return vm.CpuCount <= cpu && vm.MemoryMb <= memory;
    }

    // Call within the same transaction that saves the reservation.
    public static async Task<TrainingVirtualMachine?> Acquire(SchoolDbContext db, string? nodeId, string? currentId,
        DateTime now, CancellationToken ct, string? classroomSessionId = null, string? campAttemptId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return null;
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(item =>
            item.Id == nodeId && item.Enabled && item.Provider == ResourceProviders.Simulated, ct);
        if (node == null) return null;
        var reserved = ReservedIds(db, now, classroomSessionId, campAttemptId);
        var (cpu, memory) = await FreeCapacity(db, node, null, ct);
        var vm = await db.VirtualMachines
            .Where(item => item.NodeId == nodeId && !reserved.Contains(item.Id)
                && (item.Status == "running" || (item.CpuCount <= cpu && item.MemoryMb <= memory)))
            .OrderByDescending(item => item.Id == currentId).ThenByDescending(item => item.Status == "running")
            .ThenBy(item => item.Name).ThenBy(item => item.Id).FirstOrDefaultAsync(ct);
        if (vm != null && vm.Status != "running")
        {
            vm.Status = "running";
            vm.StartedAt = now;
        }
        return vm;
    }

    static async Task<(long Cpu, long Memory)> FreeCapacity(SchoolDbContext db, TrainingNode node, string? excludeId, CancellationToken ct)
    {
        var used = await db.VirtualMachines.Where(vm => vm.NodeId == node.Id && vm.Status == "running" && vm.Id != excludeId)
            .GroupBy(_ => 1).Select(group => new { Cpu = group.Sum(vm => (long)vm.CpuCount), Memory = group.Sum(vm => (long)vm.MemoryMb) })
            .SingleOrDefaultAsync(ct);
        return (node.TotalCpu - (used?.Cpu ?? 0), node.TotalMemoryMb - (used?.Memory ?? 0));
    }

    public static string DetectionStatus(TrainingVirtualMachine vm, bool isSimulated)
        => !isSimulated ? "unsupported" : vm.Status == "running" ? "reachable" : "offline";
}
