using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

/// <summary>
/// Fills an empty database with the customer demonstration. Re-running against a database
/// that already has users leaves those users and their data in place. The hosted disk may
/// be empty after a republish, so the same seed runs again.
/// </summary>
public static class DemoSeed
{
    public sealed record Account(string Role, string UserName, string DisplayName);

    public static readonly Account[] Accounts =
    [
        new("Admin", DemoHost.AdminUserName, "演示管理员"),
        new("Teacher", "wangjj", "王建军"),
        new("Teacher", "limin", "李敏"),
        new("Teacher", "chenhao", "陈浩"),
        new("Student", "zhaozh", "赵子涵"),
        new("Student", "qiansy", "钱思远"),
        new("Student", "sunjy", "孙佳怡"),
        new("Student", "zhoumx", "周明轩"),
        new("Student", "wuyt", "吴雨桐"),
        new("Student", "zhenghr", "郑浩然"),
        new("Student", "caozm", "曹子墨"),
        new("Student", "dengxy", "邓欣怡"),
        new("Student", "fengxx", "冯晓萱"),
        new("Student", "jiangzh", "蒋梓豪"),
        new("Student", "liuc", "刘晨"),
        new("Student", "huangsq", "黄思琪"),
        new("Student", "hejj", "何俊杰"),
        new("Student", "linsh", "林诗涵"),
        new("Student", "gaoyh", "高宇航")
    ];

    public static async Task EnsureAsync(SchoolDbContext db, IPasswordHasher<AppUser> hasher, string password, CancellationToken cancellationToken = default)
    {
        if (Validation.Password(password) is not null) throw new InvalidOperationException("演示口令不符合密码规则。");
        if (await db.Users.AnyAsync(cancellationToken)) return;
        var users = new Dictionary<string, AppUser>(StringComparer.Ordinal);
        foreach (var account in Accounts)
        {
            var user = new AppUser
            {
                UserName = account.UserName,
                DisplayName = account.DisplayName,
                Role = account.Role,
                MustChangePassword = false
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            users[account.UserName] = user;
            db.Users.Add(user);
        }
        db.Settings.Add(new AppSetting { Id = "schoolName", Value = DemoHost.SchoolName });

        var professions = new Dictionary<string, Profession>(StringComparer.Ordinal)
        {
            ["机电"] = AddProfession(db, "机电一体化技术", "JD"),
            ["数控"] = AddProfession(db, "数控技术", "SK"),
            ["机器人"] = AddProfession(db, "工业机器人技术", "JQR")
        };
        var classes = new Dictionary<string, SchoolClass>(StringComparer.Ordinal)
        {
            ["机电1"] = AddClass(db, "机电 2024 级 1 班", professions["机电"]),
            ["机电2"] = AddClass(db, "机电 2024 级 2 班", professions["机电"]),
            ["数控1"] = AddClass(db, "数控 2024 级 1 班", professions["数控"]),
            ["机器人1"] = AddClass(db, "工业机器人 2024 级 1 班", professions["机器人"])
        };
        AddPerson(db, users["wangjj"], "teacher", "T2024001", null);
        AddPerson(db, users["limin"], "teacher", "T2024002", null);
        AddPerson(db, users["chenhao"], "teacher", "T2024003", null);
        var students = new (string UserName, string Number, string ClassKey)[]
        {
            ("zhaozh", "2024010101", "机电1"), ("qiansy", "2024010102", "机电1"), ("sunjy", "2024010103", "机电1"), ("zhoumx", "2024010104", "机电1"),
            ("wuyt", "2024010201", "机电2"), ("zhenghr", "2024010202", "机电2"), ("caozm", "2024010203", "机电2"), ("dengxy", "2024010204", "机电2"),
            ("fengxx", "2024020101", "数控1"), ("jiangzh", "2024020102", "数控1"), ("liuc", "2024020103", "数控1"), ("huangsq", "2024020104", "数控1"),
            ("hejj", "2024030101", "机器人1"), ("linsh", "2024030102", "机器人1"), ("gaoyh", "2024030103", "机器人1")
        };
        var people = new Dictionary<string, Person>(StringComparer.Ordinal);
        foreach (var (userName, number, classKey) in students)
            people[userName] = AddPerson(db, users[userName], "student", number, classes[classKey]);

        AddSimulation(db);
        var course = AddCourse(db);
        AddClassroom(db, course, users["wangjj"], classes["机电1"], people);
        AddExam(db, users["demo"], classes["机电1"], people);
        AddCamp(db);
        db.Audits.Add(new AuditEntry { UserName = DemoHost.AdminUserName, Action = "初始化演示数据", Target = DemoHost.SchoolName, Detail = "空库自动写入演示账号、模拟机柜和教学数据。" });
        await db.SaveChangesAsync(cancellationToken);
    }

    static Profession AddProfession(SchoolDbContext db, string name, string code)
    {
        var profession = new Profession { Name = name, Code = code, Remark = "演示专业" };
        db.Professions.Add(profession);
        return profession;
    }

    static SchoolClass AddClass(SchoolDbContext db, string name, Profession profession)
    {
        var schoolClass = new SchoolClass { Name = name, ProfessionId = profession.Id, Remark = "演示班级" };
        db.Classes.Add(schoolClass);
        return schoolClass;
    }

    static Person AddPerson(SchoolDbContext db, AppUser user, string kind, string number, SchoolClass? schoolClass)
    {
        var person = new Person { UserId = user.Id, Kind = kind, Name = user.DisplayName, Number = number, ClassId = schoolClass?.Id };
        db.People.Add(person);
        return person;
    }

    static void AddSimulation(SchoolDbContext db)
    {
        var type = new DeviceType { Name = "模拟网络设备", Remark = "内置模拟器；无需真实硬件。" };
        var cabinet = new Cabinet { Id = "simulation-cabinet", Name = "模拟实训机柜", Host = "simulator.local", IsSimulated = true, Remark = "平台内置模拟环境，不连接物理串口控制器。" };
        var node = new TrainingNode { Id = "simulation-node", Name = "模拟计算节点 NODE-01", Host = "simulator.local", Provider = ResourceProviders.Simulated, Remark = "内置资源状态机，未启用 Hyper-V 或实际虚拟化。" };
        db.DeviceTypes.Add(type);
        db.Cabinets.Add(cabinet);
        db.Nodes.Add(node);
        db.Devices.Add(new TrainingDevice { Id = "simulation-switch", Name = "模拟交换机 SW-01", CabinetId = cabinet.Id, DeviceTypeId = type.Id, ConnectionType = ResourceProviders.Simulated, SerialPortNumber = 1, NodeId = node.Id, Remark = "内置模拟 CLI；支持接口配置、查看和保存。" });
        db.Devices.Add(new TrainingDevice { Id = "simulation-router", Name = "模拟路由器 RT-01", CabinetId = cabinet.Id, DeviceTypeId = type.Id, ConnectionType = ResourceProviders.Simulated, SerialPortNumber = 2, NodeId = node.Id, Remark = "内置模拟 CLI；支持接口配置、查看和保存。" });
        db.VirtualMachines.Add(new TrainingVirtualMachine { Id = "simulation-vm-linux", NodeId = node.Id, Name = "模拟 Linux 实训环境", IpAddress = "10.0.0.11" });
        db.VirtualMachines.Add(new TrainingVirtualMachine { Id = "simulation-vm-network", NodeId = node.Id, Name = "模拟网络实验环境", IpAddress = "10.0.0.12" });
    }

    static TrainingCourse AddCourse(SchoolDbContext db)
    {
        var series = new CoreCourseSeries { Name = "智能制造专业核心课", Description = "演示用专业核心课系列。" };
        var course = new TrainingCourse
        {
            Name = "机械制图",
            Description = "演示课程：阅读零件图并完成一次练习。",
            Type = "core",
            Status = "published",
            IsActive = true,
            PublishedAt = DateTime.UtcNow,
            SeriesId = series.Id,
            Remark = "演示数据"
        };
        var chapter = new CourseChapter { CourseId = course.Id, Name = "视图与剖视", SortOrder = 1 };
        var section = new CourseSection { ChapterId = chapter.Id, Name = "剖视图", DurationMinutes = 30, SortOrder = 1 };
        var resource = new CourseResource
        {
            SectionId = section.Id,
            Name = "剖视图识读",
            Kind = "theory",
            DurationMinutes = 15,
            Content = "全剖视图用于表达内部结构。演示环境不连接真实机床。",
            SortOrder = 1
        };
        db.CoreCourseSeries.Add(series);
        db.Courses.Add(course);
        db.CourseChapters.Add(chapter);
        db.CourseSections.Add(section);
        db.CourseResources.Add(resource);
        db.CourseQuestions.Add(new CourseQuestion
        {
            ResourceId = resource.Id,
            Type = "single",
            Stem = "表达机件内部结构时，优先选用哪种视图？",
            OptionsJson = """["局部放大图","剖视图","向视图"]""",
            AnswerJson = "[1]",
            Analysis = "剖视图用于表达内部结构。",
            Score = 10,
            SortOrder = 1
        });
        return course;
    }

    static void AddClassroom(SchoolDbContext db, TrainingCourse course, AppUser teacher, SchoolClass schoolClass, Dictionary<string, Person> people)
    {
        var now = DateTime.UtcNow;
        var classroom = new Classroom
        {
            Name = "机电 2024 级机械制图课堂",
            CourseId = course.Id,
            TeacherUserId = teacher.Id,
            OpenAt = now.AddDays(-1),
            CloseAt = now.AddDays(30),
            Status = "open"
        };
        db.Classrooms.Add(classroom);
        foreach (var (userName, person) in people.Where(item => item.Value.ClassId == schoolClass.Id))
            db.ClassroomStudents.Add(new ClassroomStudent { ClassroomId = classroom.Id, StudentUserId = people[userName].UserId, StudentPersonId = person.Id, ClassId = schoolClass.Id });
    }

    static void AddExam(SchoolDbContext db, AppUser admin, SchoolClass schoolClass, Dictionary<string, Person> people)
    {
        var bank = new ExamQuestionBank { Name = "机械制图理论题库", Type = "theory" };
        var question = new ExamQuestion
        {
            BankId = bank.Id,
            Type = "single",
            Stem = "三视图中，主视图与俯视图应遵守哪条投影规律？",
            OptionsJson = """["高平齐","长对正","宽相等"]""",
            AnswerJson = "[1]",
            Analysis = "主视图与俯视图长对正。",
            Score = 10
        };
        var paper = new ExamPaper { Name = "机械制图阶段测验卷", Description = "演示试卷，共一题。" };
        var paperQuestion = new ExamPaperQuestion
        {
            PaperId = paper.Id,
            QuestionId = question.Id,
            Type = question.Type,
            Stem = question.Stem,
            OptionsJson = question.OptionsJson,
            AnswerJson = question.AnswerJson,
            Analysis = question.Analysis,
            Score = question.Score,
            SortOrder = 1
        };
        var now = DateTime.UtcNow;
        var exam = new TrainingExam
        {
            Name = "2024 学年机械制图阶段测验",
            PaperId = paper.Id,
            Description = "演示考试，学生可以进入作答。",
            StartAt = now.AddHours(-1),
            EndAt = now.AddDays(14),
            DurationMinutes = 45,
            Status = "open",
            CreatedByUserId = admin.Id
        };
        db.ExamQuestionBanks.Add(bank);
        db.ExamQuestions.Add(question);
        db.ExamPapers.Add(paper);
        db.ExamPaperQuestions.Add(paperQuestion);
        db.TrainingExams.Add(exam);
        foreach (var person in people.Values.Where(item => item.ClassId == schoolClass.Id))
        {
            db.ExamStudents.Add(new ExamStudent
            {
                ExamId = exam.Id,
                StudentUserId = person.UserId,
                StudentPersonId = person.Id,
                ClassId = schoolClass.Id,
                ClassName = schoolClass.Name
            });
        }
    }

    static void AddCamp(SchoolDbContext db)
    {
        var position = new JobPosition
        {
            Name = "数控机床调试员岗位训练营",
            Description = "演示岗位：阅读一道安全题。不连接真实机床。",
            DurationMinutes = 40,
            StartupNodeId = "simulation-node",
            Published = true
        };
        var skill = new JobSkill { PositionId = position.Id, Name = "操作安全", SubName = "开机前检查", Ability = "识别急停和防护", SortOrder = 1 };
        db.JobPositions.Add(position);
        db.JobSkills.Add(skill);
        db.JobQuestions.Add(new JobQuestion
        {
            PositionId = position.Id,
            SkillId = skill.Id,
            Type = "single",
            Stem = "发现机床异常时，首先应该？",
            OptionsJson = """["继续加工","按下急停","关闭总电源柜"]""",
            AnswerJson = "[1]",
            Analysis = "先按下急停，再报告教师。",
            Score = 10,
            SortOrder = 1
        });
    }
}
