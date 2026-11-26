using System.Net;
using Microsoft.EntityFrameworkCore;
using School.Training;

namespace School.Training.Tests;

[Collection("http")]
public sealed class HomeProgressTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Staff_home_uses_saved_progress_and_keeps_closed_classroom_rates()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var teacherName = "home" + suffix;
        var studentName = "homes" + suffix;
        var profession = await Http.Post(scenario.Admin, "/api/professions",
            new { name = "首页专业" + suffix, code = "HP" + suffix }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, profession.Status);
        var professionId = profession.Body.GetProperty("id").GetString()!;
        var schoolClass = await Http.Post(scenario.Admin, "/api/classes",
            new { name = "首页班" + suffix, professionId }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, schoolClass.Status);
        var classId = schoolClass.Body.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "teacher", name = "首页教师", userName = teacherName, number = "T" + suffix,
            role = "Teacher", password = "TeacherPass2026"
        }, scenario.AdminCsrf)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "student", name = "首页学生", userName = studentName, number = "S" + suffix,
            classId, role = "Student", password = "StudentPass2026"
        }, scenario.AdminCsrf)).Status);

        using var teacher = scenario.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var student = scenario.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await ChangePassword(teacher, teacherName, "TeacherPass2026", "TeacherPass2026x");
        var studentCsrf = await ChangePassword(student, studentName, "StudentPass2026", "StudentPass2026x");

        string classroomId = "", questionId = "", sectionId = "";
        await scenario.WithDb(async db =>
        {
            var teacherUser = await db.Users.SingleAsync(item => item.UserName == teacherName);
            var studentUser = await db.Users.SingleAsync(item => item.UserName == studentName);
            var person = await db.People.SingleAsync(item => item.UserId == studentUser.Id);
            var course = new TrainingCourse { Name = "首页课程" + suffix, Type = "general", Status = "published", IsActive = true };
            var chapter = new CourseChapter { CourseId = course.Id, Name = "第一章", SortOrder = 1 };
            var section = new CourseSection { ChapterId = chapter.Id, Name = "第一节", SortOrder = 1 };
            var theory = new CourseResource { SectionId = section.Id, Name = "理论", Kind = "theory", SortOrder = 1 };
            var practice = new CourseResource { SectionId = section.Id, Name = "练习", Kind = "theoryPractice", SortOrder = 2 };
            var question = new CourseQuestion
            {
                ResourceId = practice.Id, Type = "single", Stem = "选 A",
                OptionsJson = """["A","B"]""", AnswerJson = "[0]", Score = 10, SortOrder = 1
            };
            var classroom = new Classroom
            {
                CourseId = course.Id, Name = "首页课堂" + suffix, TeacherUserId = teacherUser.Id, Status = "open",
                OpenAt = DateTime.UtcNow.AddHours(-1), CloseAt = DateTime.UtcNow.AddHours(3)
            };
            var now = DateTime.UtcNow;
            db.AddRange(course, chapter, section, theory, practice, question, classroom);
            db.ClassroomStudents.Add(new ClassroomStudent
            {
                ClassroomId = classroom.Id, StudentUserId = studentUser.Id,
                StudentPersonId = person.Id, ClassId = person.ClassId
            });
            db.LearningRecords.Add(new LearningRecord
            {
                ClassroomId = classroom.Id, UserId = studentUser.Id, ResourceId = theory.Id,
                Status = "completed", StartedAt = now, CompletedAt = now
            });
            await db.SaveChangesAsync();
            classroomId = classroom.Id;
            questionId = question.Id;
            sectionId = section.Id;
        });

        var first = await Http.Get(teacher, "/api/home/teacher");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(1, first.Body.GetProperty("overview").GetProperty("managedStudents").GetInt32());
        Assert.Equal(50, first.Body.GetProperty("overview").GetProperty("learningRate").GetInt32());
        Assert.Equal(0, first.Body.GetProperty("overview").GetProperty("resourcePassRate").GetInt32());
        Assert.Equal(0, first.Body.GetProperty("overview").GetProperty("passedStudents").GetInt32());
        Assert.Equal(1, first.Body.GetProperty("today").GetProperty("activeStudents").GetInt32());
        Assert.Equal(0, first.Body.GetProperty("today").GetProperty("passedStudents").GetInt32());
        Assert.Equal(0, first.Body.GetProperty("classrooms")[0].GetProperty("classPassRate").GetInt32());
        DateTime builtAt = default;
        await scenario.WithDb(async db =>
        {
            var stat = await db.ClassroomStudentStats.SingleAsync(item => item.ClassroomId == classroomId);
            Assert.Equal(1, stat.LearnedCount);
            Assert.Equal(0, stat.PassedCount);
            builtAt = stat.UpdatedAt;
        });

        var second = await Http.Get(teacher, "/api/home/teacher");
        Assert.Equal(HttpStatusCode.OK, second.Status);
        await scenario.WithDb(async db =>
        {
            var stat = await db.ClassroomStudentStats.SingleAsync(item => item.ClassroomId == classroomId);
            Assert.Equal(builtAt, stat.UpdatedAt);
        });

        var submit = await Http.Post(student, $"/api/classrooms/{classroomId}/resources/{await PracticeId(classroomId)}/submit",
            new { answers = new[] { new { questionId, selected = new[] { 0 } } } }, studentCsrf);
        Assert.Equal(HttpStatusCode.OK, submit.Status);
        Assert.True(submit.Body.GetProperty("passed").GetBoolean());

        var passed = await Http.Get(teacher, "/api/home/teacher");
        Assert.Equal(HttpStatusCode.OK, passed.Status);
        Assert.Equal(100, passed.Body.GetProperty("overview").GetProperty("learningRate").GetInt32());
        Assert.Equal(100, passed.Body.GetProperty("overview").GetProperty("resourcePassRate").GetInt32());
        Assert.Equal(1, passed.Body.GetProperty("overview").GetProperty("passedStudents").GetInt32());
        Assert.Equal(1, passed.Body.GetProperty("today").GetProperty("passedStudents").GetInt32());
        Assert.Equal(100, passed.Body.GetProperty("classrooms")[0].GetProperty("classPassRate").GetInt32());
        Assert.Equal(2, passed.Body.GetProperty("leaderboard")[0].GetProperty("learned").GetInt32());
        Assert.Equal(1, passed.Body.GetProperty("leaderboard")[0].GetProperty("passed").GetInt32());
        Assert.True(passed.Body.GetProperty("leaderboard")[0].GetProperty("activeDays").GetInt32() >= 1);

        var ranking = await Http.Get(scenario.Admin, "/api/home/admin");
        Assert.Equal(HttpStatusCode.OK, ranking.Status);
        var row = ranking.Body.GetProperty("classRanking").EnumerateArray().Single(item => item.GetProperty("classId").GetString() == classId);
        Assert.Equal(1, row.GetProperty("studentCount").GetInt32());
        Assert.Equal(1, row.GetProperty("passedStudents").GetInt32());
        Assert.Equal(100, row.GetProperty("passRate").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, $"/api/classrooms/{classroomId}/close", new { }, scenario.AdminCsrf)).Status);
        await scenario.WithDb(async db =>
        {
            var freeze = await db.ClassroomProgressFreezes.SingleAsync(item => item.ClassroomId == classroomId);
            Assert.Equal(2, freeze.ResourceCount);
            Assert.Equal(1, freeze.VerifiableCount);
            db.CourseResources.Add(new CourseResource { SectionId = sectionId, Name = "后加练习", Kind = "theoryPractice", SortOrder = 3 });
            await db.SaveChangesAsync();
        });

        var frozen = await Http.Get(teacher, "/api/home/teacher");
        Assert.Equal(HttpStatusCode.OK, frozen.Status);
        Assert.Equal(100, frozen.Body.GetProperty("overview").GetProperty("learningRate").GetInt32());
        Assert.Equal(100, frozen.Body.GetProperty("overview").GetProperty("resourcePassRate").GetInt32());
        Assert.Equal(1, frozen.Body.GetProperty("overview").GetProperty("passedStudents").GetInt32());
        Assert.Equal(100, frozen.Body.GetProperty("classrooms")[0].GetProperty("classPassRate").GetInt32());
    }

    async Task<string> PracticeId(string classroomId)
    {
        string resourceId = "";
        await scenario.WithDb(async db =>
        {
            resourceId = await (
                from resource in db.CourseResources
                join section in db.CourseSections on resource.SectionId equals section.Id
                join chapter in db.CourseChapters on section.ChapterId equals chapter.Id
                join classroom in db.Classrooms on chapter.CourseId equals classroom.CourseId
                where classroom.Id == classroomId && resource.Kind == "theoryPractice"
                select resource.Id).SingleAsync();
        });
        return resourceId;
    }

    static async Task<string> ChangePassword(HttpClient client, string userName, string currentPassword, string newPassword)
    {
        var login = await Http.Post(client, "/api/auth/login", new { userName, password = currentPassword });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var csrf = login.Body.GetProperty("csrfToken").GetString()!;
        var changed = await Http.Post(client, "/api/auth/password", new { currentPassword, newPassword }, csrf);
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        return changed.Body.GetProperty("csrfToken").GetString()!;
    }
}
