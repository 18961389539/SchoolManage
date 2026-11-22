using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class MyResourcesEndpoints
{
    public static void MapMyResources(this RouteGroupBuilder api)
    {
        api.MapGet("/my-resources", GetMyResources).RequireAuthorization(policy => policy.RequireRole("Teacher", "Student"));
    }

    private static async Task<IResult> GetMyResources(HttpContext ctx, SchoolDbContext db, SimulationSessions sessions, CancellationToken ct)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var assignedNodes = TrainingResourceAccess.AssignedNodes(db, userId);
        var nodes = await assignedNodes.OrderBy(node => node.Name)
            .Select(node => new { node.Id, node.Name, node.Enabled })
            .ToListAsync(ct);
        var cabinetIds = TrainingResourceAccess.ClassroomCabinetIds(db, userId);
        var accessible = TrainingResourceAccess.AccessibleDevices(db, userId, ctx.User.FindFirstValue("stamp") ?? "");
        var deviceRows = await (from device in db.Devices.AsNoTracking()
                                join node in db.Nodes.AsNoTracking() on device.NodeId equals node.Id into deviceNodes
                                from node in deviceNodes.DefaultIfEmpty()
                                join cabinet in db.Cabinets.AsNoTracking() on device.CabinetId equals cabinet.Id
                                where device.ConnectionType == ResourceProviders.Simulated &&
                                    (assignedNodes.Any(item => item.Id == device.NodeId) || cabinetIds.Contains(device.CabinetId))
                                orderby node.Name, device.Name
                                select new { device.Id, device.Name, device.NodeId, nodeName = node == null ? "" : node.Name,
                                    cabinetName = cabinet.Name, canAccess = accessible.Any(item => item.Id == device.Id) })
            .ToListAsync(ct);
        var reserved = SimulatedVirtualMachines.ReservedIds(db, DateTime.UtcNow);
        var virtualMachines = await (from node in assignedNodes
                                     join vm in db.VirtualMachines.AsNoTracking() on node.Id equals vm.NodeId
                                     orderby node.Name, vm.Name
                                     select new
                                     {
                                         vm.Id, vm.Name, vm.Uuid, vm.IpAddress, vm.NodeId, nodeName = node.Name,
                                         vm.Status, vm.CpuCount, vm.MemoryMb, vm.DiskGb, isSimulated = true,
                                         canAccess = node.Enabled && !reserved.Contains(vm.Id)
                                     })
            .ToListAsync(ct);
        var devices = deviceRows.Select(device => new
        {
            device.Id,
            device.Name,
            device.NodeId,
            device.nodeName,
            device.cabinetName,
            device.canAccess,
            isSimulated = true,
            sessionActive = sessions.Owner(device.Id) is not null
        }).ToArray();
        return Results.Ok(new { nodes, devices, virtualMachines });
    }
}
