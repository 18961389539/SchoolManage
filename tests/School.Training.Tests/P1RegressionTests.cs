using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace School.Training.Tests;

public sealed class AnalyticsClassroomScopeTests
{
    [Fact]
    public void Learner_records_from_different_classrooms_do_not_overwrite_each_other()
    {
        var data = new LearnerData();
        var first = new LearningRecord { ClassroomId = "class-a", UserId = "student", ResourceId = "resource" };
        var second = new LearningRecord { ClassroomId = "class-b", UserId = "student", ResourceId = "resource", Status = "inProgress" };
        data.Records[LearnerData.Key(first.ClassroomId, first.UserId, first.ResourceId)] = first;
        data.Records[LearnerData.Key(second.ClassroomId, second.UserId, second.ResourceId)] = second;

        Assert.Same(first, data.RecordFor("class-a", "student", "resource"));
        Assert.Same(second, data.RecordFor("class-b", "student", "resource"));
        Assert.Null(data.RecordFor("class-c", "student", "resource"));
        Assert.Same(first, data.BestFor("student", "resource", ["class-a"]));
    }
}

public sealed class ImportPasswordReceiptTests
{
    [Fact]
    public void Generated_passwords_are_unique_and_meet_the_validation_rule()
    {
        var passwords = Enumerable.Range(0, 500).Select(_ => ImportPasswordReceipts.GeneratePassword()).ToArray();
        Assert.Equal(passwords.Length, passwords.Distinct(StringComparer.Ordinal).Count());
        Assert.All(passwords, password =>
        {
            Assert.InRange(password.Length, 12, 128);
            Assert.Contains(password, char.IsLetter);
            Assert.Contains(password, char.IsDigit);
        });
    }

    [Fact]
    public void Receipt_is_bound_to_the_owner_and_can_only_be_read_once()
    {
        var receipts = new ImportPasswordReceipts();
        receipts.Put("batch", "owner", [new ImportCredential("student01", "InitialPass2026")], DateTime.UtcNow.AddMinutes(5));

        Assert.Null(receipts.Take("batch", "other"));
        var items = receipts.Take("batch", "owner");
        Assert.NotNull(items);
        Assert.Equal("student01", items![0].Account);
        Assert.Null(receipts.Take("batch", "owner"));
    }
}

public sealed class DatabaseSafetyCompatibilityTests
{
    [Fact]
    public async Task Version_17_database_without_job_position_published_validates_for_migration()
    {
        var directory = Directory.CreateTempSubdirectory("school-v17-");
        try
        {
            var paths = new AppPaths(directory.FullName);
            var connection = new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString();
            await using (var db = new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(connection).Options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync($"PRAGMA application_id={DatabaseSafety.ApplicationId};");
                await db.Database.ExecuteSqlRawAsync("PRAGMA user_version=17;");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"JobPositions\" DROP COLUMN \"Published\";");
            }
            SqliteConnection.ClearAllPools();

            await DatabaseSafety.Validate(paths.Database, allowOlderSchema: true);
            await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseSafety.Validate(paths.Database));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }
}

[Collection("http")]
public sealed class P1RegressionTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Published_course_must_be_unpublished_before_content_or_course_deletion()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var course = await Http.Post(scenario.Admin, "/api/courses", new { name = $"发布保护-{suffix}", type = "general" }, scenario.AdminCsrf);
        var courseId = course.Body.GetProperty("id").GetString()!;
        var chapter = await Http.Post(scenario.Admin, $"/api/courses/{courseId}/chapters", new { name = "第一章" }, scenario.AdminCsrf);
        var chapterId = chapter.Body.GetProperty("id").GetString()!;
        var section = await Http.Post(scenario.Admin, $"/api/chapters/{chapterId}/sections", new { name = "第一节", durationMinutes = 30 }, scenario.AdminCsrf);
        var sectionId = section.Body.GetProperty("id").GetString()!;
        var resource = await Http.Post(scenario.Admin, $"/api/sections/{sectionId}/resources",
            new { name = "理论资源", kind = "theory", durationMinutes = 15, content = "正文" }, scenario.AdminCsrf);
        var resourceId = resource.Body.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, $"/api/courses/{courseId}/publish", new { }, scenario.AdminCsrf)).Status);

        var blockedResource = await Http.Delete(scenario.Admin, $"/api/resources/{resourceId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, blockedResource.Status);
        Assert.Contains("先下线", blockedResource.Message);
        var blockedCourse = await Http.Delete(scenario.Admin, $"/api/courses/{courseId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, blockedCourse.Status);

        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, $"/api/courses/{courseId}/unpublish", new { }, scenario.AdminCsrf)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Http.Delete(scenario.Admin, $"/api/resources/{resourceId}", scenario.AdminCsrf)).Status);
    }

    [Fact]
    public async Task Node_referenced_by_course_resource_cannot_be_deleted()
    {
        string nodeId = "", resourceId = "";
        await scenario.WithDb(async db =>
        {
            var node = new TrainingNode { Name = "引用保护节点-" + Guid.NewGuid().ToString("N")[..8], Host = "192.0.2.55" };
            var course = new TrainingCourse { Name = "节点课程-" + Guid.NewGuid().ToString("N")[..8], Status = "draft" };
            var chapter = new CourseChapter { CourseId = course.Id, Name = "第一章", SortOrder = 1 };
            var section = new CourseSection { ChapterId = chapter.Id, Name = "第一节", SortOrder = 1 };
            var resource = new CourseResource { SectionId = section.Id, Name = "实操资源", Kind = "casePractice", StartupNodeId = node.Id };
            db.AddRange(node, course, chapter, section, resource);
            await db.SaveChangesAsync();
            nodeId = node.Id;
            resourceId = resource.Id;
        });

        var response = await Http.Delete(scenario.Admin, $"/api/nodes/{nodeId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Contains("引用", response.Message);
        await scenario.WithDb(async db =>
        {
            Assert.NotNull(await db.Nodes.FindAsync(nodeId));
            Assert.Equal(nodeId, (await db.CourseResources.FindAsync(resourceId))!.StartupNodeId);
        });
    }

    [Fact]
    public async Task Zero_roster_exam_cannot_open()
    {
        string examId = "";
        await scenario.WithDb(async db =>
        {
            var adminId = await db.Users.Where(x => x.UserName == "admin").Select(x => x.Id).SingleAsync();
            var paper = new ExamPaper { Name = "零名单试卷-" + Guid.NewGuid().ToString("N")[..8] };
            var question = new ExamQuestion { Stem = "题目", Score = 10, OptionsJson = "[\"A\",\"B\"]", AnswerJson = "[0]" };
            db.AddRange(paper, question);
            await db.SaveChangesAsync();
            db.ExamPaperQuestions.Add(new ExamPaperQuestion
            {
                PaperId = paper.Id, QuestionId = question.Id, Type = "single", Stem = question.Stem,
                OptionsJson = question.OptionsJson, AnswerJson = question.AnswerJson, Score = 10
            });
            var exam = new TrainingExam
            {
                Name = "零名单考试-" + Guid.NewGuid().ToString("N")[..8], PaperId = paper.Id,
                Status = "scheduled", StartAt = DateTime.UtcNow, EndAt = DateTime.UtcNow.AddHours(1), CreatedByUserId = adminId
            };
            db.TrainingExams.Add(exam);
            await db.SaveChangesAsync();
            examId = exam.Id;
        });

        var response = await Http.Post(scenario.Admin, $"/api/exams/{examId}/open", new { }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Contains("名单中没有考生", response.Message);
        await scenario.WithDb(async db => Assert.Equal("scheduled", (await db.TrainingExams.FindAsync(examId))!.Status));
    }

    [Fact]
    public async Task Student_preview_practice_does_not_reveal_answers()
    {
        string courseId = "", resourceId = "";
        await scenario.WithDb(async db =>
        {
            var course = new TrainingCourse { Name = "预览防泄题-" + Guid.NewGuid().ToString("N")[..8], Status = "published" };
            var chapter = new CourseChapter { CourseId = course.Id, Name = "第一章", SortOrder = 1 };
            var section = new CourseSection { ChapterId = chapter.Id, Name = "第一节", SortOrder = 1 };
            var resource = new CourseResource { SectionId = section.Id, Name = "理论练习", Kind = "theoryPractice" };
            var question = new CourseQuestion
            {
                ResourceId = resource.Id, Type = "single", Stem = "正确答案不能被预览提交拿走",
                OptionsJson = "[\"A\",\"B\"]", AnswerJson = "[1]", Analysis = "保密解析", Score = 10
            };
            db.AddRange(course, chapter, section, resource, question);
            await db.SaveChangesAsync();
            courseId = course.Id;
            resourceId = resource.Id;
        });

        var response = await Http.Post(scenario.Student,
            $"/api/courses/{courseId}/resources/{resourceId}/preview-practice",
            new { answers = Array.Empty<object>() }, scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Empty(response.Body.GetProperty("questions").EnumerateArray());
    }

    [Fact]
    public async Task Camp_save_rejects_answers_written_after_the_loaded_baseline()
    {
        string positionId = "";
        await scenario.WithDb(async db =>
        {
            var studentId = await db.Users.Where(x => x.UserName == "student01").Select(x => x.Id).SingleAsync();
            var position = new JobPosition { Name = "答案冲突岗位-" + Guid.NewGuid().ToString("N")[..8], Published = true, DurationMinutes = 60 };
            var question = new JobQuestion
            {
                PositionId = position.Id, Type = "single", Stem = "题目",
                OptionsJson = "[\"A\",\"B\"]", AnswerJson = "[0]", Score = 10
            };
            var attempt = new JobAttempt
            {
                PositionId = position.Id, UserId = studentId, StudentName = "接口测试学生", StudentUserName = "student01",
                Status = "inProgress", StartedAt = DateTime.UtcNow.AddMinutes(-5), Deadline = DateTime.UtcNow.AddMinutes(30)
            };
            db.AddRange(position, question, attempt);
            await db.SaveChangesAsync();
            db.JobAnswers.Add(new JobAnswer
            {
                AttemptId = attempt.Id, QuestionId = question.Id, SelectedJson = "[0]", AnsweredAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            positionId = position.Id;
        });

        var response = await Http.Post(scenario.Student, $"/api/camp/positions/{positionId}/answers",
            new { answers = Array.Empty<object>(), loadedAt = DateTime.UtcNow.AddMinutes(-1) }, scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("stale", response.Body.GetProperty("code").GetString());
    }
}