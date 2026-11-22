using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static partial class InfrastructureEndpoints
{
    private static void MapNodeAccounts(RouteGroupBuilder group)
    {
        group.MapGet("/nodes/{id}/accounts", async (string id, SchoolDbContext db, HttpRequest request, CancellationToken ct) =>
        {
            var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (node is null) return Missing();
            var query = from assignment in db.NodeAccountAssignments.AsNoTracking()
                        join user in db.Users on assignment.UserId equals user.Id
                        join person in db.People on user.Id equals person.UserId
                        where assignment.NodeId == id
                        select new { assignment.Id, assignment.UserId, personId = person.Id, person.Kind, person.Name, user.UserName, person.Number, user.Role, assignment.AssignedAt };
            if (Search(request) is { Length: > 0 } search) query = query.Where(x => x.Name.Contains(search) || x.UserName.Contains(search) || x.Number.Contains(search));
            var total = await query.CountAsync(ct);
            var items = await Slice(query.OrderBy(x => x.UserName), request).ToListAsync(ct);
            var accountCount = await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ct);
            return Results.Ok(new { total, items, node.AccountQuota, accountCount, node.Enabled, semantics = "现有师生账号的平台资源关联，不创建宿主机或虚拟机操作系统账户。" });
        });

        group.MapGet("/nodes/{id}/account-candidates", async (string id, SchoolDbContext db, HttpRequest request, CancellationToken ct) =>
        {
            if (!await db.Nodes.AnyAsync(x => x.Id == id, ct)) return Missing();
            var kind = request.Query["kind"].FirstOrDefault();
            if (!string.IsNullOrEmpty(kind) && kind is not ("teacher" or "student")) return Bad("师生类型应为 teacher 或 student。");
            var query = from person in db.People.AsNoTracking()
                        join user in db.Users on person.UserId equals user.Id
                        where (person.Kind == "teacher" || person.Kind == "student") && !db.NodeAccountAssignments.Any(x => x.NodeId == id && x.UserId == user.Id)
                        select new { userId = user.Id, personId = person.Id, person.Kind, person.Name, user.UserName, person.Number, user.Role };
            if (!string.IsNullOrEmpty(kind)) query = query.Where(x => x.Kind == kind);
            if (Search(request) is { Length: > 0 } search) query = query.Where(x => x.Name.Contains(search) || x.UserName.Contains(search) || x.Number.Contains(search));
            return Results.Ok(await Page(query.OrderBy(x => x.UserName), request, ct));
        });

        group.MapPost("/nodes/{id}/accounts", async (string id, NodeAccountsInput input, SchoolDbContext db, HttpContext ctx, LicenseQuotaPolicy quota) =>
        {
            if (input.UserIds is not { Length: > 0 and <= 500 } || input.UserIds.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64)) return Bad("每次请选择 1 至 500 个有效师生账号。");
            var userIds = input.UserIds.Distinct(StringComparer.Ordinal).ToArray();
            if (userIds.Length != input.UserIds.Length) return Bad("账号选择中存在重复项，请刷新后重试。");
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
                var node = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (node is null) return Missing();
                if (!node.Enabled) return Conflict("节点已禁止新增分配；仍可移除已有账号关联。");
                if (await db.NodeAccountAssignments.AnyAsync(x => x.NodeId == id && userIds.Contains(x.UserId), ctx.RequestAborted)) return Conflict("所选账号已分配到该节点，请刷新列表后重试。");
                var eligible = await (from person in db.People
                                      join user in db.Users on person.UserId equals user.Id
                                      where userIds.Contains(user.Id) && (person.Kind == "teacher" || person.Kind == "student")
                                      select user.Id).Distinct().ToListAsync(ctx.RequestAborted);
                if (eligible.Count != userIds.Length) return Bad("仅可分配已存在的教师或学生账号；系统初始管理员和已删除账号不可分配。");
                var accountCount = await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ctx.RequestAborted);
                if (accountCount + userIds.Length > node.AccountQuota) return Conflict("分配后将超过节点账号上限；本次没有添加任何账号。");
                if (await quota.CheckAllocationAsync(db, LicensedResource.Assignments, userIds.Length, ctx.RequestAborted) is { } denied) return denied;
                db.NodeAccountAssignments.AddRange(userIds.Select(userId => new NodeAccountAssignment { NodeId = id, UserId = userId }));
                Audit.Add(db, ctx, "分配节点师生账号", node.Name, $"原子新增 {userIds.Length} 个平台资源关联，不创建操作系统账户。");
                await db.SaveChangesAsync(ctx.RequestAborted);
                await transaction.CommitAsync(ctx.RequestAborted);
                return Results.Ok(new { assignedCount = userIds.Length, nodeId = id, accountCount = accountCount + userIds.Length, node.AccountQuota, message = "已建立师生账号与节点的平台资源关联。" });
            }
            finally { VmPowerLock.Release(); }
        });

        group.MapDelete("/nodes/{id}/accounts/{userId}", async (string id, string userId, SchoolDbContext db, SimulationSessions sessions, HttpContext ctx) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                var node = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (node is null) return Missing();
                var assignment = await db.NodeAccountAssignments.SingleOrDefaultAsync(x => x.NodeId == id && x.UserId == userId, ctx.RequestAborted);
                if (assignment is null) return Results.NotFound(new { message = "该账号关联已不存在，请刷新列表。" });
                db.NodeAccountAssignments.Remove(assignment);
                Audit.Add(db, ctx, "移除节点师生账号", node.Name, "释放平台资源关联；师生账号和已存设备配置保持不变。");
                await db.SaveChangesAsync(ctx.RequestAborted);
                sessions.RevokeNodeLearners(id, userId);
                return Results.Ok(new { nodeId = id, accountCount = await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ctx.RequestAborted), node.AccountQuota, message = "已移除账号关联，师生账号本身仍保留。" });
            }
            finally { VmPowerLock.Release(); }
        });
    }
}
