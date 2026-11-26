using System.Net;

namespace School.Training.Tests;

[Collection("http")]
public sealed class CampRetakeTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Student_can_retake_a_position_and_the_first_report_keeps_its_questions()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await Http.Post(scenario.Admin, "/api/camp/positions", Position(suffix, "第一题"), scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var id = created.Body.GetProperty("id").GetString()!;
        var published = await Http.Post(scenario.Admin, $"/api/camp/positions/{id}/publish", new { published = true }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, published.Status);

        var firstStart = await Http.Post(scenario.Student, $"/api/camp/positions/{id}/start", new { }, scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.OK, firstStart.Status);
        var resumed = await Http.Post(scenario.Student, $"/api/camp/positions/{id}/start", new { }, scenario.StudentCsrf);
        Assert.Equal(firstStart.Body.GetProperty("attemptId").GetString(), resumed.Body.GetProperty("attemptId").GetString());

        var taking = await Http.Get(scenario.Student, $"/api/camp/positions/{id}");
        var questionId = taking.Body.GetProperty("questions")[0].GetProperty("id").GetString();
        var submitted = await Http.Post(scenario.Student, $"/api/camp/positions/{id}/submit", new
        {
            answers = new[] { new { questionId, selected = new[] { 0 }, text = "" } },
            loadedAt = DateTime.UtcNow.AddMinutes(-1)
        }, scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.OK, submitted.Status);
        var firstAttemptId = submitted.Body.GetProperty("attemptId").GetString()!;

        var detail = await Http.Get(scenario.Admin, $"/api/camp/positions/{id}");
        Assert.True(detail.Body.GetProperty("canEditStructure").GetBoolean());
        var updated = await Http.Put(scenario.Admin, $"/api/camp/positions/{id}",
            Position(suffix, "第二版题目", detail.Body.GetProperty("updatedAt").GetDateTime()), scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, updated.Status);

        var firstReport = await Http.Get(scenario.Student, $"/api/camp/attempts/{firstAttemptId}/report");
        Assert.Equal(HttpStatusCode.OK, firstReport.Status);
        Assert.Equal(10, firstReport.Body.GetProperty("skills")[0].GetProperty("score").GetInt32());
        var firstRecord = await Http.Get(scenario.Student, $"/api/camp/attempts/{firstAttemptId}/record");
        Assert.Equal(HttpStatusCode.OK, firstRecord.Status);
        Assert.Equal("第一题", firstRecord.Body.GetProperty("questions")[0].GetProperty("stem").GetString());

        var secondStart = await Http.Post(scenario.Student, $"/api/camp/positions/{id}/start", new { }, scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.OK, secondStart.Status);
        Assert.NotEqual(firstAttemptId, secondStart.Body.GetProperty("attemptId").GetString());
        var secondTaking = await Http.Get(scenario.Student, $"/api/camp/positions/{id}");
        Assert.Equal("第二版题目", secondTaking.Body.GetProperty("questions")[0].GetProperty("stem").GetString());
        var secondQuestionId = secondTaking.Body.GetProperty("questions")[0].GetProperty("id").GetString();
        var secondSubmit = await Http.Post(scenario.Student, $"/api/camp/positions/{id}/submit", new
        {
            answers = new[] { new { questionId = secondQuestionId, selected = new[] { 1 }, text = "" } },
            loadedAt = DateTime.UtcNow.AddMinutes(-1)
        }, scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.OK, secondSubmit.Status);

        var report = await Http.Get(scenario.Student, $"/api/camp/attempts/{secondSubmit.Body.GetProperty("attemptId").GetString()}/report");
        Assert.Equal(HttpStatusCode.OK, report.Status);
        Assert.Equal(2, report.Body.GetProperty("attemptNumber").GetInt32());
        Assert.Equal(2, report.Body.GetProperty("history").GetArrayLength());
        Assert.Equal(10, report.Body.GetProperty("history")[0].GetProperty("radar")[0].GetProperty("score").GetInt32());
        Assert.Equal("第一题", (await Http.Get(scenario.Student, $"/api/camp/attempts/{firstAttemptId}/record"))
            .Body.GetProperty("questions")[0].GetProperty("stem").GetString());
    }

    static object Position(string suffix, string stem, DateTime? updatedAt = null) => new
    {
        name = "复训岗位-" + suffix,
        description = "可多次测评",
        durationMinutes = 30,
        skills = new[] { new { name = "排查", subName = "日志", ability = "定位故障" } },
        questions = new[]
        {
            new { type = "single", stem, options = new[] { "甲", "乙" }, answers = new[] { 0 }, score = 10, skillIndex = 0 }
        },
        updatedAt
    };
}
