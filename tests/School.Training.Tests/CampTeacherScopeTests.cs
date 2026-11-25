using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace School.Training.Tests;

[Collection("http")]
public sealed class CampTeacherScopeTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    const string Password = "TeacherPass2026";
    const string ChangedPassword = "TeacherPass2026x";

    [Fact]
    public async Task Teacher_can_only_list_and_view_attempts_in_their_control_scope()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var teacherAName = $"scope-teacher-a-{suffix}";
        var teacherBName = $"scope-teacher-b-{suffix}";
        var studentOutName = $"scope-student-{suffix}";
        var teacherA = await CreateAndLogin("teacher", teacherAName, $"TA{suffix}", $"范围教师甲-{suffix}");
        var teacherB = await CreateAndLogin("teacher", teacherBName, $"TB{suffix}", $"范围教师乙-{suffix}");
        var studentOut = await CreateAndLogin("student", studentOutName, $"SO{suffix}", $"范围学生乙-{suffix}");

        string inScopeId = "", outScopeId = "";
        await scenario.WithDb(async db =>
        {
            var teacherAId = await db.Users.Where(x => x.UserName == teacherAName).Select(x => x.Id).SingleAsync();
            var teacherBId = await db.Users.Where(x => x.UserName == teacherBName).Select(x => x.Id).SingleAsync();
            var inStudent = await db.Users.SingleAsync(x => x.UserName == "student01");
            var outStudent = await db.Users.SingleAsync(x => x.UserName == studentOutName);
            var inPerson = await db.People.SingleAsync(x => x.UserId == inStudent.Id);
            var outPerson = await db.People.SingleAsync(x => x.UserId == outStudent.Id);

            var courseA = new TrainingCourse { Name = $"范围课程甲-{suffix}", Type = "general", Status = "published" };
            var courseB = new TrainingCourse { Name = $"范围课程乙-{suffix}", Type = "general", Status = "published" };
            var classroomA = new Classroom
            {
                CourseId = courseA.Id, Name = $"范围课堂甲-{suffix}", TeacherUserId = teacherAId, Status = "open",
                OpenAt = DateTime.UtcNow.AddHours(-1), CloseAt = DateTime.UtcNow.AddHours(2)
            };
            var classroomB = new Classroom
            {
                CourseId = courseB.Id, Name = $"范围课堂乙-{suffix}", TeacherUserId = teacherBId, Status = "open",
                OpenAt = DateTime.UtcNow.AddHours(-1), CloseAt = DateTime.UtcNow.AddHours(2)
            };
            var position = new JobPosition { Name = $"范围岗位-{suffix}", Published = true, DurationMinutes = 60 };
            var inAttempt = GradedAttempt(position.Id, inStudent, "接口测试学生", "student01");
            var outAttempt = GradedAttempt(position.Id, outStudent, $"范围学生乙-{suffix}", studentOutName);
            db.AddRange(courseA, courseB, classroomA, classroomB, position, inAttempt, outAttempt);
            db.ClassroomStudents.Add(new ClassroomStudent
            {
                ClassroomId = classroomA.Id, StudentUserId = inStudent.Id,
                StudentPersonId = inPerson.Id, ClassId = inPerson.ClassId
            });
            db.ClassroomStudents.Add(new ClassroomStudent
            {
                ClassroomId = classroomB.Id, StudentUserId = outStudent.Id,
                StudentPersonId = outPerson.Id, ClassId = outPerson.ClassId
            });
            await db.SaveChangesAsync();
            inScopeId = inAttempt.Id;
            outScopeId = outAttempt.Id;
        });

        try
        {
            await AssertListContains(scenario.Admin, "/api/camp/reports", inScopeId, outScopeId);
            await AssertListContains(scenario.Admin, "/api/camp/records", inScopeId, outScopeId);
            await AssertListScoped(teacherA, "/api/camp/reports", inScopeId, outScopeId);
            await AssertListScoped(teacherA, "/api/camp/records", inScopeId, outScopeId);
            await AssertListScoped(teacherB, "/api/camp/reports", outScopeId, inScopeId);
            await AssertListScoped(teacherB, "/api/camp/records", outScopeId, inScopeId);

            Assert.Equal(HttpStatusCode.OK, (await Http.Get(scenario.Admin, $"/api/camp/attempts/{inScopeId}/report")).Status);
            Assert.Equal(HttpStatusCode.OK, (await Http.Get(scenario.Admin, $"/api/camp/attempts/{outScopeId}/record")).Status);

            Assert.Equal(HttpStatusCode.OK, (await Http.Get(teacherA, $"/api/camp/attempts/{inScopeId}/report")).Status);
            Assert.Equal(HttpStatusCode.OK, (await Http.Get(teacherA, $"/api/camp/attempts/{inScopeId}/record")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(teacherA, $"/api/camp/attempts/{outScopeId}/report")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(teacherA, $"/api/camp/attempts/{outScopeId}/record")).Status);

            Assert.Equal(HttpStatusCode.OK, (await Http.Get(teacherB, $"/api/camp/attempts/{outScopeId}/report")).Status);
            Assert.Equal(HttpStatusCode.OK, (await Http.Get(teacherB, $"/api/camp/attempts/{outScopeId}/record")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(teacherB, $"/api/camp/attempts/{inScopeId}/report")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(teacherB, $"/api/camp/attempts/{inScopeId}/record")).Status);

            Assert.Equal(HttpStatusCode.OK, (await Http.Get(scenario.Student, $"/api/camp/attempts/{inScopeId}/report")).Status);
            Assert.Equal(HttpStatusCode.OK, (await Http.Get(scenario.Student, $"/api/camp/attempts/{inScopeId}/record")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(scenario.Student, $"/api/camp/attempts/{outScopeId}/report")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(scenario.Student, $"/api/camp/attempts/{outScopeId}/record")).Status);

            Assert.Equal(HttpStatusCode.OK, (await Http.Get(studentOut, $"/api/camp/attempts/{outScopeId}/report")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(studentOut, $"/api/camp/attempts/{inScopeId}/record")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(studentOut, "/api/camp/reports")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.Get(studentOut, "/api/camp/records")).Status);
        }
        finally
        {
            teacherA.Dispose();
            teacherB.Dispose();
            studentOut.Dispose();
        }
    }

    static JobAttempt GradedAttempt(string positionId, AppUser student, string name, string userName) => new()
    {
        PositionId = positionId,
        UserId = student.Id,
        StudentName = name,
        StudentUserName = userName,
        Status = "graded",
        StartedAt = DateTime.UtcNow.AddHours(-2),
        Deadline = DateTime.UtcNow.AddHours(-1),
        SubmittedAt = DateTime.UtcNow.AddHours(-1),
        GradedAt = DateTime.UtcNow.AddHours(-1),
        TotalScore = 80,
        MaxScore = 100
    };

    async Task<HttpClient> CreateAndLogin(string kind, string userName, string number, string name)
    {
        var created = await Http.Post(scenario.Admin, "/api/people", new
        {
            kind,
            name,
            userName,
            number,
            role = kind == "teacher" ? "Teacher" : "Student",
            password = Password
        }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);

        var client = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Http.Post(client, "/api/auth/login", new { userName, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var csrf = login.Body.GetProperty("csrfToken").GetString()!;
        var changed = await Http.Post(client, "/api/auth/password",
            new { currentPassword = Password, newPassword = ChangedPassword }, csrf);
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        return client;
    }

    static async Task AssertListContains(HttpClient client, string path, params string[] ids)
    {
        var response = await Http.Get(client, path);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        var found = Ids(response);
        foreach (var id in ids)
            Assert.Contains(id, found);
    }

    static async Task AssertListScoped(HttpClient client, string path, string included, string excluded)
    {
        var response = await Http.Get(client, path);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        var found = Ids(response);
        Assert.Contains(included, found);
        Assert.DoesNotContain(excluded, found);
    }

    static string[] Ids(ApiResponse response)
        => [.. response.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("attemptId").GetString()!)];
}
