using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ClassroomCollaborationEndpoints
{
    const int MaxGroupMembers = 500;
    const int MaxEvidenceBytes = 5 * 1024 * 1024;
    const int MaxEvidencePerSession = 50;

    public static void MapClassroomCollaborationEndpoints(this RouteGroupBuilder all)
    {
        var staff = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher"));
        var learners = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Student"));
        all.MapGroup("").RequireAuthorization("Admin").MapClassroomCaseScoring(staff, learners);
        staff.MapGet("/classrooms/{id}/groups", ListGroups);
        staff.MapPost("/classrooms/{id}/groups", CreateGroup);
        staff.MapPut("/classroom-groups/{id}", UpdateGroup);
        staff.MapDelete("/classroom-groups/{id}", DeleteGroup);

        staff.MapGet("/classrooms/{id}/cabinet-options", CabinetOptions);
        staff.MapGet("/classrooms/{id}/cabinet-assignments", ListAssignments);
        staff.MapPost("/classrooms/{id}/cabinet-assignments", AssignCabinets);
        staff.MapPost("/classrooms/{id}/cabinet-assignments/cancel", CancelAssignments);
        staff.MapPost("/classrooms/{id}/cabinet-assignments/test", TestAssignments);
        staff.MapPost("/classroom-cabinet-assignments/{id}/reclaim", ReclaimAssignment);

        staff.MapGet("/classrooms/{id}/case-sessions", ListCaseSessions);
        staff.MapPost("/classrooms/case-sessions/{id}/restart", RestartCaseSession);
        staff.MapGet("/classrooms/case-sessions/{id}/environment", CaseEnvironment);
        staff.MapGet("/classrooms/case-sessions/{id}/evidences", ListEvidences);
        staff.MapPost("/classrooms/case-sessions/{id}/evidences", UploadEvidence);
        staff.MapGet("/case-evidences/{id}/download", DownloadEvidence);
        staff.MapDelete("/case-evidences/{id}", DeleteEvidence);

        learners.MapGet("/classrooms/{id}/resources/{resourceId}/environment", LearnerEnvironment);
        learners.MapGet("/classrooms/{id}/resources/{resourceId}/cabinet-devices", LearnerCabinetDevices);
    }

    static async Task<IResult> ListGroups(string id, HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂小组");
        var groups = await db.ClassroomGroups.AsNoTracking().Where(item => item.ClassroomId == id)
            .OrderBy(item => item.Name).ToListAsync(ct);
        var groupIds = groups.Select(item => item.Id).ToArray();
        var members = await (from member in db.ClassroomGroupMembers.AsNoTracking()
                             join user in db.Users.AsNoTracking() on member.StudentUserId equals user.Id
                             join person in db.People.AsNoTracking() on member.StudentUserId equals person.UserId into people
                             from person in people.DefaultIfEmpty()
                             join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                             from schoolClass in classes.DefaultIfEmpty()
                             where groupIds.Contains(member.GroupId)
                             select new
                             {
                                 member.Id, member.GroupId, member.StudentUserId,
                                 userName = user.UserName,
                                 name = person == null ? user.DisplayName : person.Name,
                                 number = person == null ? "" : person.Number,
                                 className = schoolClass == null ? "" : schoolClass.Name
                             }).ToListAsync(ct);
        var search = request.Query["search"].ToString().Trim();
        var rows = groups.Select(group => new
        {
            group.Id, group.Name, group.Remark, group.CreatedAt,
            members = members.Where(item => item.GroupId == group.Id)
                .OrderBy(item => item.name).ThenBy(item => item.number).ToArray()
        });
        if (search.Length > 0)
            rows = rows.Where(item => item.Name.Contains(search)
                || item.members.Any(member => member.name.Contains(search) || member.number.Contains(search) || member.userName.Contains(search)));
        var items = rows.ToArray();
        return Results.Ok(new { total = items.Length, items });
    }

    static async Task<IResult> CreateGroup(string id, ClassroomGroupInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂小组");
        var error = ClassroomCollaborationLogic.ValidateGroup(input);
        if (error != null) return Bad(error);
        if (await db.ClassroomGroups.AnyAsync(item => item.ClassroomId == id && item.Name == input.Name.Trim(), ct))
            return Conflict("该课堂下已存在同名小组");
        var (members, memberError) = await BuildMembers(db, id, input.UserIds, ct);
        if (memberError != null) return Bad(memberError);
        var entity = new ClassroomGroup { ClassroomId = id, Name = input.Name.Trim(), Remark = (input.Remark ?? "").Trim() };
        db.ClassroomGroups.Add(entity);
        foreach (var member in members) { member.GroupId = entity.Id; db.ClassroomGroupMembers.Add(member); }
        Audit.Add(db, ctx, "新增课堂小组", classroom.Name, entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id, memberCount = members.Count });
    }

    static async Task<IResult> UpdateGroup(string id, ClassroomGroupInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.ClassroomGroups.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("小组不存在");
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == entity.ClassroomId, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂小组");
        var error = ClassroomCollaborationLogic.ValidateGroup(input);
        if (error != null) return Bad(error);
        if (await db.ClassroomGroups.AnyAsync(item => item.ClassroomId == entity.ClassroomId && item.Id != id && item.Name == input.Name.Trim(), ct))
            return Conflict("该课堂下已存在同名小组");
        var (members, memberError) = await BuildMembers(db, entity.ClassroomId, input.UserIds, ct);
        if (memberError != null) return Bad(memberError);
        entity.Name = input.Name.Trim();
        entity.Remark = (input.Remark ?? "").Trim();
        var existing = await db.ClassroomGroupMembers.Where(item => item.GroupId == id).ToListAsync(ct);
        db.ClassroomGroupMembers.RemoveRange(existing);
        foreach (var member in members) { member.GroupId = id; db.ClassroomGroupMembers.Add(member); }
        Audit.Add(db, ctx, "编辑课堂小组", classroom.Name, entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id, memberCount = members.Count });
    }

    static async Task<IResult> DeleteGroup(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.ClassroomGroups.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("小组不存在");
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == entity.ClassroomId, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂小组");
        if (await db.ClassroomCabinetAssignments.AnyAsync(item => item.GroupId == id, ct))
            return Conflict("小组已分配机柜，请先取消或回收机柜分配");
        db.ClassroomGroups.Remove(entity);
        Audit.Add(db, ctx, "删除课堂小组", classroom.Name, entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<IResult> CabinetOptions(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂机柜");
        var groups = await db.ClassroomGroups.AsNoTracking().Where(item => item.ClassroomId == id)
            .OrderBy(item => item.Name).Select(item => new { item.Id, item.Name }).ToListAsync(ct);
        var resources = await (from resource in db.CourseResources.AsNoTracking()
                               join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                               join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                               where chapter.CourseId == classroom.CourseId && resource.Kind == "casePractice"
                               orderby chapter.SortOrder, section.SortOrder, resource.SortOrder
                               select new { resource.Id, resource.Name, resource.Kind }).ToListAsync(ct);
        var cabinets = await db.Cabinets.AsNoTracking().OrderBy(item => item.Name)
            .Select(item => new { item.Id, item.Name, item.Host, item.Port, item.JumpHost, item.JumpPort, item.IsSimulated })
            .ToListAsync(ct);
        var assigned = await (from assignment in db.ClassroomCabinetAssignments.AsNoTracking()
                              join assignedGroup in db.ClassroomGroups.AsNoTracking() on assignment.GroupId equals assignedGroup.Id
                              join room in db.Classrooms.AsNoTracking() on assignment.ClassroomId equals room.Id
                              select new { assignment.CabinetId, groupName = assignedGroup.Name, classroomName = room.Name })
            .ToListAsync(ct);
        return Results.Ok(new
        {
            groups, resources,
            cabinets = cabinets.Select(item =>
            {
                var current = assigned.FirstOrDefault(assignment => assignment.CabinetId == item.Id);
                return new
                {
                    item.Id, item.Name, item.Host, item.Port, item.JumpHost, item.JumpPort, item.IsSimulated,
                    assigned = current != null,
                    assignedGroupName = current?.groupName ?? "",
                    assignedClassroomName = current?.classroomName ?? ""
                };
            })
        });
    }

    static async Task<IResult> ListAssignments(string id, HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂机柜");
        var rows = await (from assignment in db.ClassroomCabinetAssignments.AsNoTracking()
                          join assignedGroup in db.ClassroomGroups.AsNoTracking() on assignment.GroupId equals assignedGroup.Id
                          join cabinet in db.Cabinets.AsNoTracking() on assignment.CabinetId equals cabinet.Id
                          join resource in db.CourseResources.AsNoTracking() on assignment.ResourceId equals resource.Id
                          where assignment.ClassroomId == id
                          orderby assignedGroup.Name, cabinet.Name
                          select new
                          {
                              assignment.Id, assignment.CabinetId, cabinet.Name, cabinet.Host, cabinet.Port,
                              cabinet.JumpHost, cabinet.JumpPort, cabinet.IsSimulated,
                              assignment.GroupId, groupName = assignedGroup.Name,
                              assignment.ResourceId, questionName = resource.Name, assignment.AssignedAt
                          }).ToListAsync(ct);
        var search = request.Query["search"].ToString().Trim();
        var items = search.Length == 0 ? rows : rows.Where(item =>
            item.Name.Contains(search) || item.Host.Contains(search) || item.JumpHost.Contains(search)
            || item.groupName.Contains(search) || item.questionName.Contains(search)).ToList();
        return Results.Ok(new { total = items.Count, items });
    }

    static async Task<IResult> AssignCabinets(string id, ClassroomCabinetAssignmentInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂机柜");
        var cabinetIds = (input.CabinetIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var error = ClassroomCollaborationLogic.ValidateAssignment(cabinetIds.Length, input.GroupId, input.ResourceId);
        if (error != null) return Bad(error);
        var group = await db.ClassroomGroups.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.GroupId && item.ClassroomId == id, ct);
        if (group == null) return Bad("请选择该课堂下的小组");
        var resource = await (from item in db.CourseResources.AsNoTracking()
                              join section in db.CourseSections.AsNoTracking() on item.SectionId equals section.Id
                              join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                              where item.Id == input.ResourceId && chapter.CourseId == classroom.CourseId && item.Kind == "casePractice"
                              select item).FirstOrDefaultAsync(ct);
        if (resource == null) return Bad("请选择本课堂课程中的案例实操题目");
        var cabinets = await db.Cabinets.AsNoTracking().Where(item => cabinetIds.Contains(item.Id))
            .OrderBy(item => item.Name).ToListAsync(ct);
        if (cabinets.Count != cabinetIds.Length) return Bad("部分机柜不存在，请刷新后重试");
        var occupied = await (from assignment in db.ClassroomCabinetAssignments.AsNoTracking()
                              join cabinet in db.Cabinets.AsNoTracking() on assignment.CabinetId equals cabinet.Id
                              join occupiedGroup in db.ClassroomGroups.AsNoTracking() on assignment.GroupId equals occupiedGroup.Id
                              join occupiedRoom in db.Classrooms.AsNoTracking() on assignment.ClassroomId equals occupiedRoom.Id
                              where cabinetIds.Contains(assignment.CabinetId)
                              select new { cabinet.Name, groupName = occupiedGroup.Name, classroomName = occupiedRoom.Name })
            .ToListAsync(ct);
        if (occupied.Count > 0)
            return Conflict("以下机柜已被分配：" + string.Join("；", occupied.Select(item => $"{item.Name}（{item.classroomName}·{item.groupName}）")));
        var userId = CurrentUserId(ctx);
        foreach (var cabinet in cabinets)
            db.ClassroomCabinetAssignments.Add(new ClassroomCabinetAssignment
            {
                ClassroomId = id, GroupId = group.Id, CabinetId = cabinet.Id, ResourceId = resource.Id,
                AssignedByUserId = userId
            });
        Audit.Add(db, ctx, "分配课堂机柜", classroom.Name, $"{group.Name} · {resource.Name} · {cabinets.Count} 个机柜");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { assigned = cabinets.Count });
    }

    static async Task<IResult> CancelAssignments(string id, ClassroomAssignmentCancelInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂机柜");
        var ids = (input.Ids ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (ids.Length is < 1 or > 200) return Bad("请选择 1–200 个机柜分配");
        var rows = await db.ClassroomCabinetAssignments.Where(item => item.ClassroomId == id && ids.Contains(item.Id)).ToListAsync(ct);
        if (rows.Count != ids.Length) return NotFound("部分分配记录不存在，请刷新后重试");
        db.ClassroomCabinetAssignments.RemoveRange(rows);
        Audit.Add(db, ctx, "取消课堂机柜分配", classroom.Name, $"取消 {rows.Count} 个机柜");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { cancelled = rows.Count });
    }

    static async Task<IResult> TestAssignments(string id, ClassroomAssignmentCancelInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂机柜");
        var ids = (input.Ids ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (ids.Length is < 1 or > 50) return Bad("请选择 1–50 个机柜分配");
        var rows = await (from assignment in db.ClassroomCabinetAssignments.AsNoTracking()
                          join cabinet in db.Cabinets.AsNoTracking() on assignment.CabinetId equals cabinet.Id
                          where assignment.ClassroomId == id && ids.Contains(assignment.Id)
                          orderby cabinet.Name
                          select new { AssignmentId = assignment.Id, Cabinet = cabinet }).ToListAsync(ct);
        if (rows.Count != ids.Length) return NotFound("部分分配记录不存在，请刷新后重试");
        var items = await Task.WhenAll(rows.Select(async row =>
        {
            if (row.Cabinet.IsSimulated)
                return new AssignmentProbe(row.AssignmentId, row.Cabinet.Id, row.Cabinet.Name, true, "模拟控制器响应正常。", true);
            var host = string.IsNullOrWhiteSpace(row.Cabinet.JumpHost) ? row.Cabinet.Host : row.Cabinet.JumpHost;
            var port = string.IsNullOrWhiteSpace(row.Cabinet.JumpHost) ? row.Cabinet.Port : row.Cabinet.JumpPort;
            var probe = await InfrastructureEndpoints.Probe(host, port, ct);
            return new AssignmentProbe(row.AssignmentId, row.Cabinet.Id, row.Cabinet.Name, probe.Success, probe.Message, false);
        }));
        Audit.Add(db, ctx, "测试课堂机柜连接", classroom.Name, $"共 {items.Length} 个，失败 {items.Count(item => !item.Success)} 个");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { items });
    }

    static async Task<IResult> ReclaimAssignment(string id, HttpContext ctx, SchoolDbContext db, SimulationSessions sessions, CancellationToken ct)
    {
        var assignment = await db.ClassroomCabinetAssignments.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (assignment == null) return NotFound("机柜分配不存在");
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == assignment.ClassroomId, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂机柜");
        var cabinet = await db.Cabinets.AsNoTracking().FirstOrDefaultAsync(item => item.Id == assignment.CabinetId, ct);
        var deviceIds = await db.Devices.AsNoTracking().Where(item => item.CabinetId == assignment.CabinetId)
            .Select(item => item.Id).ToListAsync(ct);
        var released = sessions.Reclaim(deviceIds);
        db.ClassroomCabinetAssignments.Remove(assignment);
        Audit.Add(db, ctx, "回收课堂机柜", classroom.Name, $"{cabinet?.Name ?? "机柜"}，释放 {released} 个终端会话");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { releasedSessions = released, message = $"已取消分配并释放 {released} 个终端会话；不会重置设备配置。" });
    }

    static async Task<IResult> ListCaseSessions(string id, HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权查看该课堂实操监控");
        var status = request.Query["status"].ToString();
        if (status.Length == 0) status = "active";
        if (status is not ("active" or "all")) return Bad("监控状态筛选无效");
        var query = from session in db.ClassroomCaseSessions.AsNoTracking()
                    join user in db.Users.AsNoTracking() on session.UserId equals user.Id
                    join person in db.People.AsNoTracking() on session.UserId equals person.UserId into people
                    from person in people.DefaultIfEmpty()
                    join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                    from schoolClass in classes.DefaultIfEmpty()
                    join resource in db.CourseResources.AsNoTracking() on session.ResourceId equals resource.Id
                    join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                    join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                    join vm in db.VirtualMachines.AsNoTracking() on session.VirtualMachineId equals vm.Id into vms
                    from vm in vms.DefaultIfEmpty()
                    join node in db.Nodes.AsNoTracking() on session.NodeId equals node.Id into nodes
                    from node in nodes.DefaultIfEmpty()
                    where session.ClassroomId == id
                    select new
                    {
                        SessionId = session.Id, session.UserId, session.Status, session.StartedAt, session.EndedAt,
                        session.RestartCount, session.VirtualMachineId, session.NodeId,
                        studentName = person == null ? user.DisplayName : person.Name,
                        studentNumber = person == null ? "" : person.Number,
                        userName = user.UserName,
                        className = schoolClass == null ? "" : schoolClass.Name,
                        chapterName = chapter.Name, sectionName = section.Name,
                        resourceId = resource.Id, resourceName = resource.Name,
                        virtualMachineName = vm == null ? "" : vm.Name,
                        virtualMachineStatus = vm == null ? "" : vm.Status,
                        nodeName = node == null ? "" : node.Name,
                        startupNodeId = resource.StartupNodeId
                    };
        if (status == "active") query = query.Where(item => item.Status == "active");
        var search = request.Query["search"].ToString().Trim();
        if (search.Length > 0)
            query = query.Where(item => item.studentName.Contains(search) || item.studentNumber.Contains(search)
                || item.userName.Contains(search) || item.className.Contains(search)
                || item.chapterName.Contains(search) || item.resourceName.Contains(search)
                || item.virtualMachineName.Contains(search));
        var rows = await query.OrderByDescending(item => item.StartedAt).Take(500).ToListAsync(ct);
        var sessionIds = rows.Select(item => item.SessionId).ToArray();
        var evidenceCounts = await db.CaseSessionEvidences.AsNoTracking().Where(item => sessionIds.Contains(item.SessionId))
            .GroupBy(item => item.SessionId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var activeCount = await db.ClassroomCaseSessions.CountAsync(item => item.ClassroomId == id && item.Status == "active", ct);
        return Results.Ok(new
        {
            classroom = new { classroom.Id, classroom.Name, classroom.Status },
            total = rows.Count, activeCount,
            items = rows.Select(item => new
            {
                item.SessionId, item.UserId, item.studentName, item.studentNumber, item.userName, item.className,
                item.chapterName, item.sectionName, item.resourceId, item.resourceName,
                item.StartedAt, item.EndedAt, item.Status, item.RestartCount,
                item.VirtualMachineId, item.virtualMachineName, item.virtualMachineStatus, item.NodeId, item.nodeName,
                evidenceCount = evidenceCounts.GetValueOrDefault(item.SessionId),
                canRestart = item.Status == "active" && (item.VirtualMachineId != null || item.startupNodeId != null)
            })
        });
    }

    static async Task<IResult> RestartCaseSession(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var session = await db.ClassroomCaseSessions.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (session == null) return NotFound("实操会话不存在");
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == session.ClassroomId, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂实操环境");
        if (session.Status != "active" || TeachingLogic.EffectiveStatus(classroom, DateTime.UtcNow) != "open")
            return Conflict("实操已结束，不能重启环境");
        var now = DateTime.UtcNow;
        var vm = await ClassroomCaseLogic.AcquireVirtualMachineAsync(db, session, now, ct);
        if (vm == null) return Conflict("该实操未配置启动节点或当前没有可用虚拟机");
        session.RestartedAt = now;
        session.RestartCount += 1;
        vm.StartedAt = now;
        var student = await db.Users.AsNoTracking().Where(item => item.Id == session.UserId).Select(item => item.DisplayName).FirstOrDefaultAsync(ct);
        Audit.Add(db, ctx, "重启课堂实操环境", classroom.Name, $"{student ?? "学生"} · {vm.Name}");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { virtualMachineId = vm.Id, virtualMachineName = vm.Name, virtualMachineStatus = vm.Status, restartedAt = now });
    }

    static async Task<IResult> CaseEnvironment(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var session = await db.ClassroomCaseSessions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (session == null) return NotFound("实操会话不存在");
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == session.ClassroomId, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权查看该课堂实操环境");
        var nodeId = session.NodeId;
        if (session.VirtualMachineId != null)
            nodeId = await db.VirtualMachines.AsNoTracking().Where(item => item.Id == session.VirtualMachineId)
                .Select(item => item.NodeId).FirstOrDefaultAsync(ct) ?? nodeId;
        return Results.Ok(await BuildEnvironment(db, nodeId, session.VirtualMachineId, ct));
    }

    static async Task<IResult> LearnerEnvironment(string id, string resourceId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        var userId = CurrentUserId(ctx);
        if (!await db.ClassroomStudents.AnyAsync(item => item.ClassroomId == id && item.StudentUserId == userId, ct))
            return Forbidden("你不在该课堂中");
        if (TeachingLogic.EffectiveStatus(classroom, DateTime.UtcNow) != "open") return Conflict("课堂尚未开放或已结课");
        var resource = await (from item in db.CourseResources.AsNoTracking()
                              join section in db.CourseSections.AsNoTracking() on item.SectionId equals section.Id
                              join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                              where item.Id == resourceId && chapter.CourseId == classroom.CourseId && item.Kind == "casePractice"
                              select item).FirstOrDefaultAsync(ct);
        if (resource == null) return NotFound("案例实操资源不存在");
        var record = await db.LearningRecords.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ClassroomId == id && item.UserId == userId && item.ResourceId == resourceId, ct);
        if (record == null) return NotFound("请先开始实操");
        var session = await db.ClassroomCaseSessions.AsNoTracking().FirstOrDefaultAsync(item => item.LearningRecordId == record.Id, ct);
        var nodeId = session?.NodeId ?? resource.StartupNodeId;
        if (session?.VirtualMachineId != null)
            nodeId = await db.VirtualMachines.AsNoTracking().Where(item => item.Id == session.VirtualMachineId)
                .Select(item => item.NodeId).FirstOrDefaultAsync(ct) ?? nodeId;
        return Results.Ok(await BuildEnvironment(db, nodeId, session?.VirtualMachineId, ct));
    }

    static async Task<IResult> LearnerCabinetDevices(string id, string resourceId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var grants = TrainingResourceAccess.ClassroomCabinetIds(db, userId, id, resourceId);
        var allowed = TrainingResourceAccess.AccessibleDevices(db, userId, ctx.User.FindFirstValue("stamp") ?? "");
        var rows = await (from device in allowed.AsNoTracking()
                          join cabinet in db.Cabinets on device.CabinetId equals cabinet.Id
                          where grants.Contains(cabinet.Id)
                          orderby cabinet.Name, device.Name
                          select new { device.Id, device.Name, cabinetName = cabinet.Name, isSimulated = true,
                              connectionType = device.ConnectionType }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    static async Task<object> BuildEnvironment(SchoolDbContext db, string? nodeId, string? virtualMachineId, CancellationToken ct)
    {
        var node = nodeId == null ? null : await db.Nodes.AsNoTracking().Where(item => item.Id == nodeId)
            .Select(item => new { item.Id, item.Name, item.Host, item.Enabled, item.Remark }).FirstOrDefaultAsync(ct);
        var devices = await (from device in db.Devices.AsNoTracking()
                             join cabinet in db.Cabinets.AsNoTracking() on device.CabinetId equals cabinet.Id
                             where device.ConnectionType == ResourceProviders.Simulated && nodeId != null && device.NodeId == nodeId
                             orderby device.Name
                             select new { device.Id, device.Name, cabinetName = cabinet.Name, isSimulated = true, canAccess = true })
            .ToListAsync(ct);
        var virtualMachines = await db.VirtualMachines.AsNoTracking()
            .Where(item => nodeId != null && item.NodeId == nodeId)
            .OrderBy(item => item.Name)
            .Select(item => new { item.Id, item.Name, item.Status, item.CpuCount, item.MemoryMb, item.DiskGb, selected = item.Id == virtualMachineId })
            .ToListAsync(ct);
        return new { nodeId, node, devices, virtualMachines };
    }

    static async Task<IResult> ListEvidences(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var (session, error) = await FindManagedSession(db, ctx, id, ct);
        if (error != null) return error;
        var rows = await (from evidence in db.CaseSessionEvidences.AsNoTracking()
                          join user in db.Users.AsNoTracking() on evidence.CapturedByUserId equals user.Id into users
                          from user in users.DefaultIfEmpty()
                          where evidence.SessionId == session!.Id
                          orderby evidence.CreatedAt descending
                          select new
                          {
                              evidence.Id, evidence.FileName, evidence.Size, evidence.Note, evidence.CreatedAt,
                              capturedByName = user == null ? "" : user.DisplayName
                          }).ToListAsync(ct);
        return Results.Ok(new { total = rows.Count, items = rows });
    }

    static async Task<IResult> UploadEvidence(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var (session, error) = await FindManagedSession(db, ctx, id, ct);
        if (error != null) return error;
        if (!ctx.Request.HasFormContentType) return Bad("请选择截图文件");
        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0) return Bad("请选择截图文件");
        if (file.Length > MaxEvidenceBytes) return Bad("截图不能超过 5MB");
        if (await db.CaseSessionEvidences.CountAsync(item => item.SessionId == id, ct) >= MaxEvidencePerSession)
            return Conflict($"单个实操会话最多保存 {MaxEvidencePerSession} 张截图");
        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            bytes = memory.ToArray();
        }
        var extension = ClassroomCollaborationLogic.DetectImageExtension(bytes);
        if (extension == null) return Bad("仅支持 PNG、JPEG 或 WebP 截图");
        var note = form["note"].ToString().Trim();
        if (note.Length > 300) return Bad("取证说明最多 300 个字符");
        var originalName = file.FileName.Replace('\\', '/').Split('/').Last().Trim();
        if (originalName.Length == 0) originalName = $"实操截图-{DateTime.UtcNow:yyyyMMddHHmmss}{extension}";
        if (originalName.Length > 100) originalName = originalName[..100];
        var storedName = Guid.NewGuid().ToString("N") + extension;
        var directory = Path.Combine(paths.CaseEvidence, session!.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, storedName);
        var contentType = extension switch { ".jpg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
        var entity = new CaseSessionEvidence
        {
            SessionId = session.Id, FileName = originalName, StoredName = storedName, ContentType = contentType,
            Size = bytes.Length, Note = note, CapturedByUserId = CurrentUserId(ctx)
        };
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct);
            db.CaseSessionEvidences.Add(entity);
            Audit.Add(db, ctx, "课堂实操截图取证", session.Id, originalName);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return Results.Ok(new
        {
            entity.Id, entity.FileName, entity.Size, entity.Note, entity.CreatedAt,
            capturedByName = ctx.User.Identity?.Name ?? ""
        });
    }

    static async Task<IResult> DownloadEvidence(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var row = await (from evidence in db.CaseSessionEvidences.AsNoTracking()
                         join session in db.ClassroomCaseSessions.AsNoTracking() on evidence.SessionId equals session.Id
                         join classroom in db.Classrooms.AsNoTracking() on session.ClassroomId equals classroom.Id
                         where evidence.Id == id
                         select new { evidence, Classroom = classroom }).FirstOrDefaultAsync(ct);
        if (row == null) return NotFound("取证截图不存在");
        if (!CanManage(ctx, row.Classroom)) return Forbidden("无权查看该取证截图");
        var path = Path.Combine(paths.CaseEvidence, row.evidence.SessionId, row.evidence.StoredName);
        if (!File.Exists(path)) return NotFound("截图文件缺失，请查看服务日志");
        return Results.File(path, row.evidence.ContentType);
    }

    static async Task<IResult> DeleteEvidence(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var row = await (from evidence in db.CaseSessionEvidences
                         join session in db.ClassroomCaseSessions.AsNoTracking() on evidence.SessionId equals session.Id
                         join classroom in db.Classrooms.AsNoTracking() on session.ClassroomId equals classroom.Id
                         where evidence.Id == id
                         select new { Evidence = evidence, Classroom = classroom }).FirstOrDefaultAsync(ct);
        if (row == null) return NotFound("取证截图不存在");
        if (!CanManage(ctx, row.Classroom)) return Forbidden("无权删除该取证截图");
        db.CaseSessionEvidences.Remove(row.Evidence);
        Audit.Add(db, ctx, "删除实操取证截图", row.Classroom.Name, row.Evidence.FileName);
        await db.SaveChangesAsync(ct);
        try { File.Delete(Path.Combine(paths.CaseEvidence, row.Evidence.SessionId, row.Evidence.StoredName)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<(ClassroomCaseSession? Session, IResult? Error)> FindManagedSession(SchoolDbContext db, HttpContext ctx, string id, CancellationToken ct)
    {
        var session = await db.ClassroomCaseSessions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (session == null) return (null, NotFound("实操会话不存在"));
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == session.ClassroomId, ct);
        if (classroom == null) return (null, NotFound("课堂不存在"));
        if (!CanManage(ctx, classroom)) return (null, Forbidden("无权管理该课堂实操取证"));
        return (session, null);
    }

    static async Task<(List<ClassroomGroupMember> Members, string? Error)> BuildMembers(
        SchoolDbContext db, string classroomId, string[]? userIds, CancellationToken ct)
    {
        var ids = (userIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (ids.Length > MaxGroupMembers) return ([], $"单个小组最多 {MaxGroupMembers} 名成员");
        if (ids.Length == 0) return ([], null);
        var enrolled = await db.ClassroomStudents.AsNoTracking()
            .Where(item => item.ClassroomId == classroomId && ids.Contains(item.StudentUserId))
            .Select(item => item.StudentUserId).ToListAsync(ct);
        if (enrolled.Count != ids.Length) return ([], "小组成员必须是本课堂学生");
        return (ids.Select(userId => new ClassroomGroupMember { StudentUserId = userId }).ToList(), null);
    }

    static bool CanManage(HttpContext ctx, Classroom classroom)
        => ctx.User.IsInRole("Admin") || (ctx.User.IsInRole("Teacher") && classroom.TeacherUserId == CurrentUserId(ctx));

    static string CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    sealed record AssignmentProbe(string AssignmentId, string CabinetId, string Name, bool Success, string Message, bool IsSimulated);
    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
    static IResult Forbidden(string message) => Results.Json(new { message }, statusCode: StatusCodes.Status403Forbidden);
}

public static class ClassroomCollaborationLogic
{
    public static string? ValidateGroup(ClassroomGroupInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 100) return "小组名称需为 1–100 个字符";
        if (name.Any(char.IsControl)) return "小组名称不能包含控制字符";
        if ((input.Remark ?? "").Trim().Length > 500) return "小组备注最多 500 个字符";
        return null;
    }

    public static string? ValidateAssignment(int cabinetCount, string? groupId, string? resourceId)
    {
        if (cabinetCount is < 1 or > 100) return "每次选择 1–100 个机柜";
        if (string.IsNullOrWhiteSpace(groupId)) return "请选择小组";
        if (string.IsNullOrWhiteSpace(resourceId)) return "请选择案例实操题目";
        return null;
    }

    public static string? DetectImageExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P') return ".webp";
        return null;
    }
}

public static class ClassroomCaseLogic
{
    public static async Task<string?> StartAsync(SchoolDbContext db, CourseResource resource, LearningRecord record, string userId, DateTime now, CancellationToken ct)
    {
        var session = await db.ClassroomCaseSessions.FirstOrDefaultAsync(item => item.LearningRecordId == record.Id, ct);
        if (session == null)
        {
            session = new ClassroomCaseSession
            {
                ClassroomId = record.ClassroomId, LearningRecordId = record.Id, UserId = userId,
                ResourceId = resource.Id, Status = "active", StartedAt = now
            };
            db.ClassroomCaseSessions.Add(session);
        }
        if (session.Status == "active" && session.WorkId.Length > 0) return null;
        var resuming = session.Status == "saved";
        if (!resuming)
        {
            var devices = await ClassroomCaseScoring.LoadDevices(db, resource.StartupNodeId, ct);
            if (devices.Count > ClassroomCaseScoring.MaxDevices) return "实操节点上的模拟设备数量超出上限。";
            var topology = CaseTopology.Read(resource.TopologyBlueprintJson);
            if (topology != null)
            {
                if (CaseTopology.ValidateDevices(topology, devices) is { } problem) return problem;
                devices = devices.Where(device => topology.DeviceIds.Contains(device.Id)).ToList();
            }
            session.StartedAt = now;
            session.NodeId = resource.StartupNodeId;
            session.TopologyBlueprintJson = resource.TopologyBlueprintJson;
            ClassroomCaseScoring.Initialize(session, resource, devices);
        }
        if (session.NodeId != null && !await db.Nodes.AnyAsync(node => node.Id == session.NodeId
            && node.Enabled && node.Provider == ResourceProviders.Simulated, ct))
            return "实操节点未启用或不支持模拟环境。";
        session.Status = "active";
        session.EndedAt = null;
        if (resuming) session.Revision++;
        session.VirtualMachineId = (await AcquireVirtualMachineAsync(db, session, now, ct))?.Id;
        return null;
    }

    public static async Task SuspendClassroomAsync(SchoolDbContext db, string classroomId, ClassroomLiveShare shares, CancellationToken ct)
    {
        var sessions = await db.ClassroomCaseSessions.Where(item => item.ClassroomId == classroomId
            && item.Status == "active").ToListAsync(ct);
        foreach (var session in sessions)
            ClassroomCaseScoring.Suspend(session, shares);
    }

    internal static async Task<int> CloseDueAsync(SchoolDbContext db, ClassroomLiveShare shares, int batchSize, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = db.Classrooms.Where(room => room.Status != "closed" && room.CloseAt <= now).OrderBy(room => room.CloseAt);
        if (!await due.AnyAsync(ct)) return 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var classrooms = await due.Take(batchSize).ToListAsync(ct);
        foreach (var room in classrooms)
        {
            room.Status = "closed";
            await SuspendClassroomAsync(db, room.Id, shares, ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return classrooms.Count;
    }

    public static async Task<TrainingVirtualMachine?> AcquireVirtualMachineAsync(
        SchoolDbContext db, ClassroomCaseSession session, DateTime now, CancellationToken ct)
    {
        var nodeId = session.NodeId ?? await db.CourseResources.AsNoTracking().Where(item => item.Id == session.ResourceId)
            .Select(item => item.StartupNodeId).FirstOrDefaultAsync(ct);
        var vm = await SimulatedVirtualMachines.Acquire(db, nodeId, session.VirtualMachineId, now, ct, classroomSessionId: session.Id);
        if (vm != null)
        {
            session.NodeId = vm.NodeId;
            session.VirtualMachineId = vm.Id;
        }
        return vm;
    }
}
