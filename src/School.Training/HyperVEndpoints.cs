using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class HyperVEndpoints
{
    public static void MapHyperV(this RouteGroupBuilder admin)
    {
        var routes = admin.MapGroup("/hyperv");
        routes.MapGet("/virtual-machines", ListMachines);
        routes.MapPost("/nodes/{nodeId}/virtual-machines/{uuid:guid}/{action}", Control);
        routes.MapGet("/nodes/{nodeId}/virtual-machines/{uuid:guid}/connection", Connection);
    }

    static IQueryable<TrainingNode> Nodes(SchoolDbContext db)
        => db.Nodes.AsNoTracking().Where(node => node.Provider == HyperVProvider.Name);

    static async Task<IResult> ListMachines(HttpRequest request, SchoolDbContext db, HyperVProvider provider, CancellationToken ct)
    {
        var requestedNode = request.Query["nodeId"].FirstOrDefault();
        var query = Nodes(db);
        if (!string.IsNullOrEmpty(requestedNode)) query = query.Where(node => node.Id == requestedNode);
        var nodes = await query.OrderBy(node => node.Name).ToListAsync(ct);
        var offline = new List<string>();
        var available = new List<(TrainingNode Node, HyperVMachine Machine)>();
        foreach (var node in nodes)
        {
            var snapshot = await provider.InspectAsync(node.Host, ct);
            if (snapshot?.Machines == null) offline.Add(node.Name);
            else available.AddRange(snapshot.Machines.Select(vm => (node, vm)));
        }

        var search = request.Query["search"].ToString().Trim();
        var filtered = available.Where(pair => search.Length == 0
            || pair.Machine.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || pair.Machine.Uuid.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
            || pair.Machine.IpAddress.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Machine.Name).ThenBy(pair => pair.Node.Id).ThenBy(pair => pair.Machine.Uuid).ToArray();
        var (page, size) = Validation.Page(request);
        return Results.Ok(new
        {
            total = filtered.Length,
            unavailableNodes = offline,
            items = filtered.Skip((page - 1) * size).Take(size).Select(pair => new
            {
                id = pair.Machine.Uuid, pair.Machine.Uuid, pair.Machine.Name, pair.Machine.IpAddress, pair.Machine.CpuCount,
                pair.Machine.MemoryMb, nodeId = pair.Node.Id, nodeName = pair.Node.Name, nodeEnabled = pair.Node.Enabled,
                status = pair.Machine.Status switch { "Running" => "running", "Off" => "stopped", _ => pair.Machine.Status },
                detectionStatus = CanConnect(pair.Machine) ? "reachable" : "offline", isSimulated = false, provider = HyperVProvider.Name
            })
        });
    }

    static async Task<IResult> Control(string nodeId, Guid uuid, string action, HttpContext ctx, SchoolDbContext db, HyperVProvider provider, CancellationToken ct)
    {
        if (action is not ("start" or "stop" or "restart" or "destroy")) return Results.NotFound();
        var node = await Nodes(db).SingleOrDefaultAsync(item => item.Id == nodeId, ct);
        if (node == null) return Results.NotFound();
        if (!node.Enabled && action is "start" or "restart") return Results.Conflict(new { message = "请先启用 Hyper-V 节点分配。" });
        var result = await provider.ControlAsync(node.Host, uuid, action, ct);
        if (result == null) return Unavailable();
        if (result.Error == "notFound") return Results.NotFound(new { message = "找不到该实训虚拟机。" });
        if (!result.Completed) return Results.Conflict(new { message = "当前 Hyper-V 虚拟机状态不允许此操作。" });
        var label = action switch { "start" => "启动", "stop" => "停止", "restart" => "重启", _ => "移除" };
        Audit.Add(db, ctx, label + " Hyper-V 虚拟机", node.Name, uuid.ToString());
        await db.SaveChangesAsync(CancellationToken.None);
        return Results.Ok(new { message = label + "操作已完成。" });
    }

    static async Task<IResult> Connection(string nodeId, Guid uuid, SchoolDbContext db, HyperVProvider provider, CancellationToken ct)
    {
        var node = await Nodes(db).SingleOrDefaultAsync(item => item.Id == nodeId, ct);
        if (node == null) return Results.NotFound();
        var snapshot = await provider.InspectAsync(node.Host, ct);
        if (snapshot == null) return Unavailable();
        var machine = snapshot.Machines?.SingleOrDefault(item => item.Uuid == uuid);
        if (machine == null) return Results.NotFound();
        if (!CanConnect(machine)) return Results.Conflict(new { message = "虚拟机尚未运行或未上报可用的 IPv4 地址。" });
        var address = IPAddress.Parse(machine.IpAddress);
        var rdp = $"full address:s:{address}\r\nprompt for credentials:i:1\r\nauthentication level:i:2\r\nredirectclipboard:i:0\r\n";
        return Results.File(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(rdp)).ToArray(),
            "application/x-rdp", $"SchoolTraining-{uuid:N}.rdp");
    }

    static bool CanConnect(HyperVMachine machine)
        => machine.Status == "Running" && IPAddress.TryParse(machine.IpAddress, out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    internal static IResult Unavailable()
        => Results.Json(new { message = "无法访问 Hyper-V 节点。请核对学校服务器允许列表、Hyper-V 模块、远程管理证书和服务账户权限。" }, statusCode: 503);
}
