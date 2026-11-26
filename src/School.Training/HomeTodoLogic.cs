using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class HomeTodoLogic
{
    public static object View(string key, string kind, string title, string hint, string? route = null,
        object? parameters = null, string? actionLabel = null)
        => new
        {
            key, kind, title, hint,
            action = route == null ? null : new { route, @params = parameters, label = actionLabel ?? "去处理" }
        };

    public static bool LicenseNearCapacity(LicenseQuota quota)
    {
        if (quota.Limit is not int limit || limit < 0) return false;
        if (limit == 0) return true;
        var remaining = limit - quota.Used;
        return quota.Used * 10 >= limit * 8 || remaining <= 2;
    }

    public static async Task<object[]> StudentTodosAsync(SchoolDbContext db, string userId, IEnumerable<Classroom> classrooms, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var items = new List<object>();
        foreach (var classroom in classrooms)
        {
            var status = TeachingLogic.EffectiveStatus(classroom, now);
            if (status == "open")
            {
                items.Add(View("classroom-open:" + classroom.Id, "classroom-open",
                    "课堂「" + classroom.Name + "」已开课", "可以进入学习。",
                    "classroom-content", new { classroomId = classroom.Id }, "进入课堂"));
            }
            else if (status == "scheduled" && classroom.OpenAt <= now.AddHours(48) && classroom.OpenAt > now)
            {
                items.Add(View("classroom-upcoming:" + classroom.Id, "classroom-upcoming",
                    "课堂「" + classroom.Name + "」即将开课", "开课后即可进入学习。",
                    "classroom-content", new { classroomId = classroom.Id }, "查看课堂"));
            }
        }

        var assignments = await (from enrollment in db.ExamStudents.AsNoTracking()
                                 join exam in db.TrainingExams.AsNoTracking() on enrollment.ExamId equals exam.Id
                                 where enrollment.StudentUserId == userId
                                 select new { Exam = exam, enrollment.DeploymentStatus }).ToListAsync(ct);
        var examIds = assignments.Select(item => item.Exam.Id).ToArray();
        var attempts = await db.ExamAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && examIds.Contains(item.ExamId)).ToListAsync(ct);
        var paperIds = assignments.Select(item => item.Exam.PaperId).Distinct().ToArray();
        var practicalPapers = (await db.ExamPaperQuestions.AsNoTracking()
            .Where(item => paperIds.Contains(item.PaperId) && item.PracticalNodeId != "")
            .Select(item => item.PaperId).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var attemptByExam = attempts.ToDictionary(item => item.ExamId, StringComparer.Ordinal);

        foreach (var item in assignments)
        {
            var exam = item.Exam;
            var attempt = attemptByExam.GetValueOrDefault(exam.Id);
            var status = ExamLogic.EffectiveStatus(exam, now);
            var requiresEnvironment = practicalPapers.Contains(exam.PaperId);
            var canStart = status == "open"
                           && (attempt == null || (attempt.Status == "inProgress" && attempt.Deadline > now))
                           && (item.DeploymentStatus == "deployed" || !requiresEnvironment);
            var canReview = status == "closed" && attempt is { Status: "graded" };
            var taking = new { examId = exam.Id };

            if (attempt is { Status: "inProgress" } && attempt.Deadline > now)
            {
                items.Add(View("exam-in-progress:" + exam.Id, "exam-in-progress",
                    "考试「" + exam.Name + "」正在进行", "请尽快继续作答，避免超时。",
                    "exam-taking", taking, "继续作答"));
            }
            else if (canStart)
            {
                var hint = requiresEnvironment ? "实操环境已就绪，可以进入考试。" : "考试已开放，可以进入作答。";
                items.Add(View("exam-ready:" + exam.Id, "exam-ready",
                    "考试「" + exam.Name + "」可以进入", hint, "exam-taking", taking, "去作答"));
            }
            else if (status == "open" && requiresEnvironment && item.DeploymentStatus != "deployed"
                     && (attempt == null || attempt.Status == "inProgress"))
            {
                items.Add(View("exam-waiting-deploy:" + exam.Id, "exam-waiting-deploy",
                    "考试「" + exam.Name + "」等待部署实操环境", "部署完成后可在考试页刷新状态，无需一直停留等待。",
                    "exam-taking", taking, "查看状态"));
            }
            else if (status == "scheduled" && exam.StartAt <= now.AddDays(7) && exam.StartAt > now)
            {
                items.Add(View("exam-upcoming:" + exam.Id, "exam-upcoming",
                    "考试「" + exam.Name + "」即将开始", "请提前确认名单与作答时间。",
                    "my-exams", null, "我的考试"));
            }
            else if (canReview)
            {
                items.Add(View("exam-reviewable:" + exam.Id, "exam-reviewable",
                    "考试「" + exam.Name + "」已出分，可以复盘", "教师已完成批阅，可查看成绩与解析。",
                    "exam-records", null, "去复盘"));
            }
        }

        return Prioritize(items, 10);
    }

    public static async Task<(object[] Todos, bool CanCreateClassroom)> TeacherTodosAsync(
        SchoolDbContext db, string userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var items = new List<object>();
        var canCreate = await db.Courses.AsNoTracking().AnyAsync(item => item.Status == "published" && (
            (item.Visibility == ContentAccess.Published && item.IsActive)
            || item.Visibility == ContentAccess.Shared
            || (item.Visibility == ContentAccess.Private && item.OwnerUserId == userId)), ct);
        var exams = await db.TrainingExams.AsNoTracking().Where(item => item.CreatedByUserId == userId).ToListAsync(ct);
        await CollectStaffExamTodos(db, exams, items, now, "exams", ct);
        return (Prioritize(items, 10), canCreate);
    }

    public static async Task<object[]> AdminTodosAsync(SchoolDbContext db, LicenseQuotaPolicy licenses, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var items = new List<object>();

        var inactive = await db.Courses.AsNoTracking()
            .Where(item => item.Status == "published" && item.Visibility == ContentAccess.Published && !item.IsActive)
            .OrderBy(item => item.Name).Select(item => item.Name).ToListAsync(ct);
        if (inactive.Count > 0)
        {
            var names = string.Join("、", inactive.Take(3));
            var extra = inactive.Count > 3 ? " 等 " + inactive.Count + " 门" : "";
            items.Add(View("course-inactive", "course-inactive",
                "有 " + inactive.Count + " 门课程已发布但尚未激活",
                "「" + names + "」" + extra + "已出现在课程目录，教师还不能用来开课。",
                "courses", null, "去课程管理"));
        }

        var adminTaught = await (from classroom in db.Classrooms.AsNoTracking()
                                 join teacher in db.Users.AsNoTracking() on classroom.TeacherUserId equals teacher.Id
                                 where teacher.Role == "Admin" && classroom.Status != "closed" && classroom.CloseAt > now
                                 orderby classroom.Name
                                 select classroom.Name).ToListAsync(ct);
        if (adminTaught.Count > 0)
        {
            var names = string.Join("、", adminTaught.Take(3));
            var extra = adminTaught.Count > 3 ? " 等 " + adminTaught.Count + " 个" : "";
            items.Add(View("classroom-admin-teacher", "classroom-admin-teacher",
                "有 " + adminTaught.Count + " 个课堂由管理员账号任课",
                "「" + names + "」" + extra + "在教师端可能看不到。请编辑课堂并指定授课教师。",
                "classrooms", null, "去课堂管理"));
        }

        var exams = await db.TrainingExams.AsNoTracking().ToListAsync(ct);
        await CollectStaffExamTodos(db, exams, items, now, "exams", ct, includeOpening: false, includeClosing: false, includeGrading: false);

        var quotas = await licenses.ReadQuotasAsync(db, ct);
        var near = quotas.Where(LicenseNearCapacity).ToArray();
        if (near.Length > 0)
        {
            var detail = string.Join("、", near.Select(item => item.Name + " " + item.Used + "/" + item.Limit));
            items.Add(View("license-near-full", "license-near-full",
                "有 " + near.Length + " 项许可证额度将满",
                detail + "。额度用尽后将无法再新增对应资源。",
                "licenses", null, "查看许可证"));
        }

        return Prioritize(items, 10);
    }

    static async Task CollectStaffExamTodos(SchoolDbContext db, List<TrainingExam> exams, List<object> items,
        DateTime now, string examRoute, CancellationToken ct,
        bool includeOpening = true, bool includeClosing = true, bool includeGrading = true)
    {
        if (exams.Count == 0) return;
        var examIds = exams.Select(item => item.Id).ToArray();
        var paperIds = exams.Select(item => item.PaperId).Distinct().ToArray();
        var practicalPapers = (await db.ExamPaperQuestions.AsNoTracking()
            .Where(item => paperIds.Contains(item.PaperId) && item.PracticalNodeId != "")
            .Select(item => item.PaperId).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        if (includeGrading)
        {
            var pending = await db.ExamAttempts.AsNoTracking()
                .Where(item => examIds.Contains(item.ExamId) && item.Status == "submitted")
                .GroupBy(item => item.ExamId)
                .Select(group => new { ExamId = group.Key, Count = group.Count() })
                .ToListAsync(ct);
            var byExam = pending.ToDictionary(item => item.ExamId, item => item.Count);
            var total = pending.Sum(item => item.Count);
            if (total > 0)
            {
                var first = exams.FirstOrDefault(item => byExam.ContainsKey(item.Id));
                var hint = first == null ? "请尽快完成人工批阅。" : "其中「" + first.Name + "」有 " + byExam[first.Id] + " 份。";
                items.Add(View("grade-pending", "grade-pending",
                    "有 " + total + " 份答卷待批阅", hint, examRoute, null, "去批阅"));
            }
        }

        var pendingDeploy = await db.ExamStudents.AsNoTracking()
            .Where(item => examIds.Contains(item.ExamId) && item.DeploymentStatus != "deployed")
            .GroupBy(item => item.ExamId)
            .Select(group => new { ExamId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ExamId, item => item.Count, ct);

        foreach (var exam in exams)
        {
            var status = ExamLogic.EffectiveStatus(exam, now);
            if (status != "closed" && practicalPapers.Contains(exam.PaperId)
                && pendingDeploy.TryGetValue(exam.Id, out var pendingCount) && pendingCount > 0)
            {
                items.Add(View("deploy-pending:" + exam.Id, "deploy-pending",
                    "考试「" + exam.Name + "」还有 " + pendingCount + " 人未部署实操环境",
                    "未部署时学生交卷后自动实操分可能为 0，且之后也补不了。",
                    examRoute, null, "去部署"));
            }
            if (includeOpening && status == "scheduled" && exam.StartAt <= now.AddDays(7) && exam.StartAt > now)
            {
                items.Add(View("exam-opening:" + exam.Id, "exam-opening",
                    "考试「" + exam.Name + "」即将开考", "请确认名单，含模拟实操的考试请先完成环境部署。",
                    examRoute, null, "查看考试"));
            }
            if (includeClosing && status == "open" && exam.EndAt <= now.AddHours(48) && exam.EndAt > now)
            {
                items.Add(View("exam-closing:" + exam.Id, "exam-closing",
                    "考试「" + exam.Name + "」即将结考", "结束前请完成监考与待批阅安排。",
                    examRoute, null, "查看考试"));
            }
        }
    }

    static object[] Prioritize(List<object> items, int limit)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["exam-in-progress"] = 0,
            ["grade-pending"] = 1,
            ["license-near-full"] = 2,
            ["exam-ready"] = 3,
            ["deploy-pending"] = 4,
            ["exam-waiting-deploy"] = 5,
            ["course-inactive"] = 6,
            ["classroom-admin-teacher"] = 7,
            ["exam-opening"] = 8,
            ["exam-closing"] = 9,
            ["exam-upcoming"] = 10,
            ["exam-reviewable"] = 11,
            ["classroom-open"] = 12,
            ["classroom-upcoming"] = 13
        };
        return items
            .Select(item => (Item: item, Kind: KindOf(item)))
            .OrderBy(entry => order.GetValueOrDefault(entry.Kind, 50))
            .ThenBy(entry => KeyOf(entry.Item), StringComparer.Ordinal)
            .Take(limit)
            .Select(entry => entry.Item)
            .ToArray();
    }

    static string KindOf(object item)
    {
        var kind = item.GetType().GetProperty("kind")?.GetValue(item) as string;
        return kind ?? "";
    }

    static string KeyOf(object item)
    {
        var key = item.GetType().GetProperty("key")?.GetValue(item) as string;
        return key ?? "";
    }
}
