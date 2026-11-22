using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed record CaseTopologyBlueprint(string[] DeviceIds, CaseTopologyLink[] Links);
public sealed record CaseTopologyLink(string FromDeviceId, string FromPort, string ToDeviceId, string ToPort, string Network);

public sealed class CaseTopologyTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string BlueprintJson { get; set; } = "null";
}

public static class CaseTopology
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CaseTopologyBlueprint? Read(string json) => JsonSerializer.Deserialize<CaseTopologyBlueprint>(json, Json);

    public static void MapCaseTopology(this RouteGroupBuilder admin)
    {
        admin.MapGet("/case-topology-templates", List);
        admin.MapPost("/case-topology-templates", Create);
        admin.MapDelete("/case-topology-templates/{id}", Delete);
    }

    sealed record TemplateInput(string Name, string NodeId, CaseTopologyBlueprint Blueprint);

    static async Task<IResult> List(string nodeId, SchoolDbContext db, CancellationToken ct)
    {
        var templates = await db.CaseTopologyTemplates.AsNoTracking().Where(item => item.NodeId == nodeId)
            .OrderBy(item => item.Name).Take(100).ToListAsync(ct);
        return Results.Ok(templates.Select(item => new { item.Id, item.Name, blueprint = Read(item.BlueprintJson) }));
    }

    static async Task<IResult> Create(TemplateInput input, SchoolDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) return Bad("模板名称需为 1–100 字");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await Validate(db, input.NodeId, input.Blueprint, ct) is { } error) return Bad(error);
        if (await db.CaseTopologyTemplates.AnyAsync(item => item.Name == name && item.NodeId == input.NodeId, ct))
            return Results.Conflict(new { message = "节点下已有同名模板" });
        if (await db.CaseTopologyTemplates.CountAsync(item => item.NodeId == input.NodeId, ct) >= 100)
            return Bad("每个节点最多保存 100 个拓扑模板");
        var template = new CaseTopologyTemplate
        {
            Name = name, NodeId = input.NodeId, BlueprintJson = ClassroomCaseScoring.Write(input.Blueprint)
        };
        db.CaseTopologyTemplates.Add(template);
        Audit.Add(db, ctx, "创建实操拓扑模板", template.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { template.Id, template.Name, blueprint = input.Blueprint });
    }

    static async Task<IResult> Delete(string id, SchoolDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var template = await db.CaseTopologyTemplates.FindAsync([id], ct);
        if (template == null) return Results.NotFound();
        db.CaseTopologyTemplates.Remove(template);
        Audit.Add(db, ctx, "删除实操拓扑模板", template.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    public static async Task<string?> ValidateResource(SchoolDbContext db, CourseResourceInput input, CancellationToken ct)
    {
        if (input.TopologyBlueprint == null) return null;
        if (input.Kind != "casePractice") return "只有案例实操可配置拓扑";
        if (await Validate(db, input.StartupNodeId, input.TopologyBlueprint, ct) is { } error) return error;
        return (input.CaseRubric ?? []).Any(item => !input.TopologyBlueprint.DeviceIds.Contains(item.DeviceId))
            ? "评分设备必须包含在实操拓扑中" : null;
    }

    static async Task<string?> Validate(SchoolDbContext db, string? nodeId, CaseTopologyBlueprint? blueprint, CancellationToken ct)
    {
        if (blueprint == null || !await db.Nodes.AnyAsync(node => node.Id == nodeId && node.Provider == ResourceProviders.Simulated, ct))
            return "请选择模拟启动节点并配置拓扑";
        var devices = await ClassroomCaseScoring.LoadDevices(db, nodeId, ct);
        if (devices.Count > ClassroomCaseScoring.MaxDevices) return "实操节点上的模拟设备数量超出上限";
        return ValidateDevices(blueprint, devices);
    }

    public static string? ValidateDevices(CaseTopologyBlueprint blueprint, IReadOnlyList<TrainingDevice> devices)
    {
        if (blueprint.DeviceIds == null || blueprint.DeviceIds.Length is < 1 or > 100
            || blueprint.Links == null || blueprint.Links.Length > 50)
            return "拓扑需包含 1–100 台设备，最多 50 条连线";
        var selected = blueprint.DeviceIds.ToHashSet(StringComparer.Ordinal);
        var ports = devices.ToDictionary(device => device.Id, device => SimulationDeviceConfiguration.Read(device).Interfaces);
        if (selected.Count != blueprint.DeviceIds.Length || selected.Any(id => id == null || !ports.ContainsKey(id)))
            return "拓扑设备重复或已不属于所选节点";
        var used = new HashSet<(string, string)>();
        foreach (var link in blueprint.Links)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.Network) || link.Network.Trim().Length > 40
                || link.Network.Any(char.IsControl))
                return "每条连线需填写 1–40 字的网络名称";
            if (link.FromDeviceId == link.ToDeviceId
                || !ValidPort(link.FromDeviceId, link.FromPort) || !ValidPort(link.ToDeviceId, link.ToPort))
                return "连线需使用两台拓扑设备上的有效接口，每个接口只可连接一次";
        }
        return null;

        bool ValidPort(string id, string port)
            => id != null && port != null && selected.Contains(id) && ports[id].ContainsKey(port) && used.Add((id, port));
    }

    static IResult Bad(string message) => Results.BadRequest(new { message });
}
