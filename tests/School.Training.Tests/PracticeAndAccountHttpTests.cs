using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using School.Training;

namespace School.Training.Tests;

[Collection("http")]
public sealed class PracticeAndAccountHttpTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Historical_pass_does_not_change_when_current_question_total_changes()
    {
        var (classroomId, resourceId, questionId, studentId) = await SeedPracticeClassroom("稳定计分");
        var submit = await Http.Post(scenario.Student,
            $"/api/classrooms/{classroomId}/resources/{resourceId}/submit",
            new { answers = new[] { new { questionId, selected = new[] { 0 } } } },
            scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.OK, submit.Status);
        Assert.True(submit.Body.GetProperty("passed").GetBoolean());
        Assert.Equal(10, submit.Body.GetProperty("score").GetInt32());
        Assert.Equal(10, submit.Body.GetProperty("maxScore").GetInt32());

        await scenario.WithDb(async db =>
        {
            var question = await db.CourseQuestions.SingleAsync(item => item.Id == questionId);
            question.Score = 100;
            await db.SaveChangesAsync();
        });

        var detail = await Http.Get(scenario.Admin, $"/api/classrooms/{classroomId}/students/{studentId}/statistics");
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        var transcript = detail.Body.GetProperty("transcript").EnumerateArray().Single(item => item.GetProperty("id").GetString() == resourceId);
        Assert.True(transcript.GetProperty("passed").GetBoolean());
        Assert.Equal(10, transcript.GetProperty("bestScore").GetInt32());
        Assert.Equal(10, transcript.GetProperty("maxScore").GetInt32());
    }

    [Fact]
    public async Task Stale_question_ids_are_rejected_and_do_not_overwrite_scores()
    {
        var (classroomId, resourceId, questionId, _) = await SeedPracticeClassroom("旧题提交");
        var first = await Http.Post(scenario.Student,
            $"/api/classrooms/{classroomId}/resources/{resourceId}/submit",
            new { answers = new[] { new { questionId, selected = new[] { 0 } } } },
            scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.OK, first.Status);

        var stale = await Http.Post(scenario.Student,
            $"/api/classrooms/{classroomId}/resources/{resourceId}/submit",
            new { answers = new[] { new { questionId = "retired-question", selected = new[] { 0 } } } },
            scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Contains("课程练习内容已更新", stale.Message);
        Assert.Contains("未覆盖原成绩", stale.Message);

        await scenario.WithDb(async db =>
        {
            var record = await db.LearningRecords.SingleAsync(item => item.ClassroomId == classroomId && item.ResourceId == resourceId);
            Assert.Equal(1, record.AttemptCount);
            Assert.Equal(10, record.BestScore);
            Assert.Equal(1, await db.PracticeAttempts.CountAsync(item => item.LearningRecordId == record.Id));
        });
    }

    [Fact]
    public async Task Legacy_review_does_not_regrade_against_current_questions()
    {
        var (classroomId, resourceId, questionId, attemptId) = await SeedLegacyAttempt("旧卷复盘");
        await scenario.WithDb(async db =>
        {
            var question = await db.CourseQuestions.SingleAsync(item => item.Id == questionId);
            question.Stem = "已经被改掉的题干";
            question.AnswerJson = "[1]";
            question.Score = 99;
            await db.SaveChangesAsync();
        });

        var review = await Http.Get(scenario.Admin, $"/api/classrooms/{classroomId}/statistics/practice-attempts/{attemptId}");
        Assert.Equal(HttpStatusCode.OK, review.Status);
        Assert.True(review.Body.GetProperty("snapshotUnavailable").GetBoolean());
        Assert.Contains("快照", review.Body.GetProperty("snapshotMessage").GetString());
        Assert.Equal(6, review.Body.GetProperty("score").GetInt32());
        Assert.Equal(10, review.Body.GetProperty("maxScore").GetInt32());
        Assert.True(review.Body.GetProperty("passed").GetBoolean());
        Assert.Equal(JsonValueKind.Array, review.Body.GetProperty("questions").ValueKind);
        Assert.Equal(0, review.Body.GetProperty("questions").GetArrayLength());
    }

    [Fact]
    public async Task Deleting_accounts_with_history_is_rejected_for_the_whole_batch()
    {
        string withHistoryPersonId = "", cleanPersonId = "", withHistoryUserId = "", cleanUserId = "";
        await scenario.WithDb(async db =>
        {
            var withHistory = NewPerson("hist01", "有历史学生", "H001");
            var clean = NewPerson("clean01", "无历史学生", "C001");
            var admin = await db.Users.SingleAsync(item => item.UserName == "admin");
            var course = new TrainingCourse { Name = "删除保护课程", Type = "general", Status = "published" };
            var chapter = new CourseChapter { CourseId = course.Id, Name = "第一章", SortOrder = 1 };
            var section = new CourseSection { ChapterId = chapter.Id, Name = "第一节", SortOrder = 1 };
            var resource = new CourseResource { SectionId = section.Id, Name = "理论资源", Kind = "theory", Content = "正文", SortOrder = 1 };
            var classroom = new Classroom
            {
                CourseId = course.Id, Name = "删除保护课堂", TeacherUserId = admin.Id, Status = "open",
                OpenAt = DateTime.UtcNow.AddHours(-1), CloseAt = DateTime.UtcNow.AddHours(2)
            };
            db.Users.AddRange(withHistory.User, clean.User);
            db.People.AddRange(withHistory.Person, clean.Person);
            db.AddRange(course, chapter, section, resource, classroom);
            db.ClassroomStudents.Add(new ClassroomStudent
            {
                ClassroomId = classroom.Id, StudentUserId = withHistory.User.Id,
                StudentPersonId = withHistory.Person.Id
            });
            db.LearningRecords.Add(new LearningRecord
            {
                ClassroomId = classroom.Id,
                UserId = withHistory.User.Id,
                ResourceId = resource.Id,
                Status = "completed",
                BestScore = 8,
                AttemptCount = 1
            });
            await db.SaveChangesAsync();
            withHistoryPersonId = withHistory.Person.Id;
            cleanPersonId = clean.Person.Id;
            withHistoryUserId = withHistory.User.Id;
            cleanUserId = clean.User.Id;
        });

        var batch = await Http.Post(scenario.Admin, "/api/people/batch-delete",
            new { ids = new[] { withHistoryPersonId, cleanPersonId } }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, batch.Status);
        Assert.Contains("不能删除", batch.Message);
        Assert.Contains("学习记录 1 条", batch.Message);
        Assert.Contains("整批未删除", batch.Message);
        Assert.Equal(1, batch.Body.GetProperty("history").GetProperty("learningRecords").GetInt32());

        var single = await Http.Delete(scenario.Admin, $"/api/people/{withHistoryPersonId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, single.Status);

        await scenario.WithDb(async db =>
        {
            Assert.True(await db.People.AnyAsync(item => item.Id == withHistoryPersonId));
            Assert.True(await db.People.AnyAsync(item => item.Id == cleanPersonId));
            Assert.True(await db.Users.AnyAsync(item => item.Id == withHistoryUserId));
            Assert.True(await db.Users.AnyAsync(item => item.Id == cleanUserId));
            Assert.Equal(1, await db.LearningRecords.CountAsync(item => item.UserId == withHistoryUserId));
        });
    }

    [Fact]
    public async Task Accounts_without_history_can_still_be_deleted()
    {
        string personId = "", userId = "";
        await scenario.WithDb(async db =>
        {
            var created = NewPerson("none01", "可删除学生", "N001");
            db.Users.Add(created.User);
            db.People.Add(created.Person);
            await db.SaveChangesAsync();
            personId = created.Person.Id;
            userId = created.User.Id;
        });

        var deleted = await Http.Delete(scenario.Admin, $"/api/people/{personId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, deleted.Status);
        Assert.Equal(1, deleted.Body.GetProperty("deletedCount").GetInt32());
        await scenario.WithDb(async db =>
        {
            Assert.False(await db.People.AnyAsync(item => item.Id == personId));
            Assert.False(await db.Users.AnyAsync(item => item.Id == userId));
        });
    }

    async Task<(string ClassroomId, string ResourceId, string QuestionId, string StudentId)> SeedPracticeClassroom(string name)
    {
        string classroomId = "", resourceId = "", questionId = "", studentId = "";
        await scenario.WithDb(async db =>
        {
            var student = await db.Users.SingleAsync(item => item.UserName == "student01");
            var person = await db.People.SingleAsync(item => item.UserId == student.Id);
            var admin = await db.Users.SingleAsync(item => item.UserName == "admin");
            var course = new TrainingCourse { Name = name + "课程", Type = "general", Status = "published" };
            var chapter = new CourseChapter { CourseId = course.Id, Name = "第一章", SortOrder = 1 };
            var section = new CourseSection { ChapterId = chapter.Id, Name = "第一节", SortOrder = 1 };
            var resource = new CourseResource { SectionId = section.Id, Name = name, Kind = "theoryPractice", SortOrder = 1 };
            var question = new CourseQuestion
            {
                ResourceId = resource.Id, Type = "single", Stem = "原题干",
                OptionsJson = """["A","B","C","D"]""", AnswerJson = "[0]", Score = 10, SortOrder = 1
            };
            var classroom = new Classroom
            {
                CourseId = course.Id, Name = name + "课堂", TeacherUserId = admin.Id, Status = "open",
                OpenAt = DateTime.UtcNow.AddHours(-1), CloseAt = DateTime.UtcNow.AddHours(2)
            };
            db.AddRange(course, chapter, section, resource, question, classroom);
            db.ClassroomStudents.Add(new ClassroomStudent
            {
                ClassroomId = classroom.Id, StudentUserId = student.Id,
                StudentPersonId = person.Id, ClassId = person.ClassId
            });
            await db.SaveChangesAsync();
            classroomId = classroom.Id;
            resourceId = resource.Id;
            questionId = question.Id;
            studentId = student.Id;
        });
        return (classroomId, resourceId, questionId, studentId);
    }

    async Task<(string ClassroomId, string ResourceId, string QuestionId, string AttemptId)> SeedLegacyAttempt(string name)
    {
        var seeded = await SeedPracticeClassroom(name);
        string attemptId = "";
        await scenario.WithDb(async db =>
        {
            var student = await db.Users.SingleAsync(item => item.UserName == "student01");
            var record = new LearningRecord
            {
                ClassroomId = seeded.ClassroomId, UserId = student.Id, ResourceId = seeded.ResourceId,
                Status = "completed", AttemptCount = 1, BestScore = 6, CompletedAt = DateTime.UtcNow
            };
            var attempt = new PracticeAttempt
            {
                LearningRecordId = record.Id,
                AnswersJson = """[{"questionId":"legacy-question","selected":[0]}]""",
                Score = 6, MaxScore = 10, SubmittedAt = DateTime.UtcNow
            };
            db.LearningRecords.Add(record);
            db.PracticeAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
        });
        return (seeded.ClassroomId, seeded.ResourceId, seeded.QuestionId, attemptId);
    }

    static (AppUser User, Person Person) NewPerson(string userName, string name, string number)
    {
        var user = new AppUser { UserName = userName, DisplayName = name, Role = "Student" };
        var person = new Person { UserId = user.Id, Kind = "student", Name = name, Number = number };
        return (user, person);
    }
}
