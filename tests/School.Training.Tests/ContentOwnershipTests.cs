using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace School.Training.Tests;

[Collection("http")]
public sealed class ContentOwnershipTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Teacher_owns_private_content_and_cannot_edit_the_standard_library()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownerName = "own" + suffix;
        var otherName = "oth" + suffix;
        var studentName = "stu" + suffix;
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "teacher", name = "归属教师", userName = ownerName, number = "TO" + suffix,
            role = "Teacher", password = "TeacherPass2026"
        }, scenario.AdminCsrf)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "teacher", name = "其他教师", userName = otherName, number = "TT" + suffix,
            role = "Teacher", password = "TeacherPass2026"
        }, scenario.AdminCsrf)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "student", name = "归属学生", userName = studentName, number = "TS" + suffix,
            role = "Student", password = "StudentPass2026"
        }, scenario.AdminCsrf)).Status);

        using var owner = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var other = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var student = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var ownerCsrf = await ChangePassword(owner, ownerName, "TeacherPass2026", "TeacherPass2026x");
        var otherCsrf = await ChangePassword(other, otherName, "TeacherPass2026", "TeacherPass2026x");
        var studentCsrf = await ChangePassword(student, studentName, "StudentPass2026", "StudentPass2026x");
        _ = studentCsrf;

        var libraryName = "标准库课程" + suffix;
        var library = await Http.Post(scenario.Admin, "/api/courses", new { name = libraryName, type = "general" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, library.Status);
        var libraryId = library.Body.GetProperty("id").GetString()!;
        var libraryChapter = await Http.Post(scenario.Admin, $"/api/courses/{libraryId}/chapters", new { name = "标准章" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, libraryChapter.Status);
        var librarySection = await Http.Post(scenario.Admin, $"/api/chapters/{libraryChapter.Body.GetProperty("id").GetString()}/sections",
            new { name = "标准节", durationMinutes = 15 }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, librarySection.Status);
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, $"/api/sections/{librarySection.Body.GetProperty("id").GetString()}/resources",
            new { name = "标准讲义", kind = "theory", durationMinutes = 15, content = "正文" }, scenario.AdminCsrf)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(scenario.Admin, $"/api/courses/{libraryId}/publish?activate=true", new { }, scenario.AdminCsrf)).Status);
        var libraryVersion = (await Http.Get(scenario.Admin, "/api/courses/" + libraryId)).Body.GetProperty("version").GetInt32();
        var locked = await Http.Put(owner, "/api/courses/" + libraryId, new
        {
            name = libraryName, type = "general", visibility = "private", version = libraryVersion
        }, ownerCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, locked.Status);
        var blockedChapter = await Http.Post(owner, $"/api/courses/{libraryId}/chapters", new { name = "越权章节" }, ownerCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, blockedChapter.Status);

        var courseName = "本人课程" + suffix;
        var created = await Http.Post(owner, "/api/courses", new { name = courseName, type = "general", visibility = "private" }, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var courseId = created.Body.GetProperty("id").GetString()!;
        var detail = await Http.Get(owner, "/api/courses/" + courseId);
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        Assert.Equal("private", detail.Body.GetProperty("visibility").GetString());
        Assert.True(detail.Body.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(owner, $"/api/courses/{courseId}/chapters", new { name = "第一章" }, ownerCsrf)).Status);
        Assert.False(await Listed(other, courseName, manage: true));
        Assert.False(await Listed(student, courseName, manage: false));

        var version = detail.Body.GetProperty("version").GetInt32();
        var shared = await Http.Put(owner, "/api/courses/" + courseId, new
        {
            name = courseName, type = "general", visibility = "shared", version
        }, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, shared.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.Get(other, "/api/courses/" + courseId)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.Put(other, "/api/courses/" + courseId, new
        {
            name = courseName, type = "general", visibility = "private", version
        }, otherCsrf)).Status);

        var adminView = await Http.Get(scenario.Admin, "/api/courses/" + courseId);
        var published = await Http.Put(scenario.Admin, "/api/courses/" + courseId, new
        {
            name = courseName, type = "general", visibility = "published", version = adminView.Body.GetProperty("version").GetInt32()
        }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, published.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.Put(owner, "/api/courses/" + courseId, new
        {
            name = courseName, type = "general", visibility = "private", version = adminView.Body.GetProperty("version").GetInt32() + 1
        }, ownerCsrf)).Status);

        var bankName = "本人题库" + suffix;
        var bank = await Http.Post(owner, "/api/exam-question-banks", new { name = bankName, type = "theory", visibility = "private" }, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, bank.Status);
        var bankId = bank.Body.GetProperty("id").GetString()!;
        Assert.False(await Listed(other, bankName, manage: false, path: "/api/exam-question-banks"));
        var standardBankName = "标准题库" + suffix;
        var standardBank = await Http.Post(scenario.Admin, "/api/exam-question-banks", new { name = standardBankName, type = "theory" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, standardBank.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.Put(owner, "/api/exam-question-banks/" + standardBank.Body.GetProperty("id").GetString(), new
        {
            name = standardBankName, type = "theory", visibility = "private"
        }, ownerCsrf)).Status);

        var question = await Http.Post(owner, "/api/exam-questions", new
        {
            type = "single", stem = "教研题" + suffix, options = new[] { "甲", "乙", "丙", "丁" }, answers = new[] { 0 }, score = 5, bankId
        }, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, question.Status);
        var questionId = question.Body.GetProperty("id").GetString()!;

        var paperName = "本人试卷" + suffix;
        var paper = await Http.Post(owner, "/api/exam-papers", new { name = paperName, description = "", remark = "", visibility = "private" }, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, paper.Status);
        var paperId = paper.Body.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Http.Post(owner, $"/api/exam-papers/{paperId}/questions", new
        {
            items = new[] { new { questionId, score = 5 } }
        }, ownerCsrf)).Status);
        Assert.False(await Listed(other, paperName, manage: false, path: "/api/exam-papers"));
        var start = DateTime.UtcNow.AddDays(2);
        var rejected = await Http.Post(other, "/api/exams", new
        {
            name = "越权考试" + suffix, paperId, description = "", startAt = start, endAt = start.AddHours(2), durationMinutes = 60
        }, otherCsrf);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
        var sharedPaper = await Http.Put(owner, "/api/exam-papers/" + paperId, new
        {
            name = paperName, description = "", remark = "", visibility = "shared"
        }, ownerCsrf);
        Assert.Equal(HttpStatusCode.NoContent, sharedPaper.Status);
        var seenPaper = await Http.Get(other, "/api/exam-papers/" + paperId);
        Assert.Equal(HttpStatusCode.OK, seenPaper.Status);
        Assert.Equal("shared", seenPaper.Body.GetProperty("visibility").GetString());
        Assert.False(seenPaper.Body.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.Put(other, "/api/exam-papers/" + paperId, new
        {
            name = paperName, description = "", remark = "", visibility = "private"
        }, otherCsrf)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Http.Put(scenario.Admin, "/api/exam-papers/" + paperId, new
        {
            name = paperName, description = "", remark = "", visibility = "published"
        }, scenario.AdminCsrf)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.Put(owner, "/api/exam-papers/" + paperId, new
        {
            name = paperName, description = "改动", remark = "", visibility = "private"
        }, ownerCsrf)).Status);
        var exam = await Http.Post(owner, "/api/exams", new
        {
            name = "教研考试" + suffix, paperId, description = "", startAt = start, endAt = start.AddHours(2), durationMinutes = 60
        }, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, exam.Status);
        Assert.True(await Listed(other, paperName, manage: false, path: "/api/exam-papers"));
    }

    static async Task<bool> Listed(HttpClient client, string name, bool manage, string path = "/api/courses")
    {
        var query = path + "?search=" + Uri.EscapeDataString(name) + "&pageSize=20" + (manage ? "&manage=true" : "");
        var response = await Http.Get(client, query);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return response.Body.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("name").GetString() == name);
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
