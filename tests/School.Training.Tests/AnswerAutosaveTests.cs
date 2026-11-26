using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace School.Training.Tests;

[Collection("http")]
public sealed class AnswerAutosaveTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Password_reset_and_logout_drop_a_cached_stamp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = "stamp" + suffix;
        var created = await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "student", name = "戳记学生", userName, number = "ST" + suffix,
            role = "Student", password = "StudentPass2026"
        }, scenario.AdminCsrf);
        Assert.True(created.Status == HttpStatusCode.OK, created.Body.ToString());
        var personId = created.Body.GetProperty("id").GetString()!;

        using var client = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var jar = new Dictionary<string, string>(StringComparer.Ordinal);
        var csrf = await SignIn(client, jar, userName, "StudentPass2026");
        csrf = await ChangePassword(client, jar, csrf, "StudentPass2026", "StudentPass2026x");
        Assert.Equal(HttpStatusCode.OK, (await Get(client, jar, "/api/auth/me")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, jar, "/api/auth/me")).Status);

        var reset = await Http.Post(scenario.Admin, $"/api/people/{personId}/reset-password",
            new { newPassword = "ResetPass2026x" }, scenario.AdminCsrf);
        Assert.True(reset.Status == HttpStatusCode.OK, reset.Body.ToString());
        var rejected = await Get(client, jar, "/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.Status);

        csrf = await SignIn(client, jar, userName, "ResetPass2026x");
        csrf = await ChangePassword(client, jar, csrf, "ResetPass2026x", "ResetPass2026y");
        Assert.Equal(HttpStatusCode.OK, (await Get(client, jar, "/api/auth/me")).Status);
        var replay = jar.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var logout = await Send(client, jar, HttpMethod.Post, "/api/auth/logout", new { }, csrf);
        Assert.True(logout.Status == HttpStatusCode.OK, logout.Body.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(client, replay, "/api/auth/me")).Status);
    }

    [Fact]
    public async Task Autosave_writes_only_changed_exam_answers()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = "save" + suffix;
        var created = await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "student", name = "增量学生", userName, number = "SV" + suffix,
            role = "Student", password = "StudentPass2026"
        }, scenario.AdminCsrf);
        Assert.True(created.Status == HttpStatusCode.OK, created.Body.ToString());

        using var student = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var studentCsrf = await ChangePassword(student, userName, "StudentPass2026", "StudentPass2026x");
        var userId = (await Http.Get(student, "/api/auth/me")).Body.GetProperty("user").GetProperty("id").GetString()!;

        var bank = await Http.Post(scenario.Admin, "/api/exam-question-banks",
            new { name = "增量题库" + suffix, type = "theory" }, scenario.AdminCsrf);
        Assert.True(bank.Status == HttpStatusCode.OK, bank.Body.ToString());
        var bankId = bank.Body.GetProperty("id").GetString()!;
        var firstQuestion = await Http.Post(scenario.Admin, "/api/exam-questions", new
        {
            type = "single", stem = "第一题" + suffix, options = new[] { "甲", "乙" }, answers = new[] { 0 },
            score = 5, bankId
        }, scenario.AdminCsrf);
        var secondQuestion = await Http.Post(scenario.Admin, "/api/exam-questions", new
        {
            type = "single", stem = "第二题" + suffix, options = new[] { "丙", "丁" }, answers = new[] { 1 },
            score = 5, bankId
        }, scenario.AdminCsrf);
        Assert.True(firstQuestion.Status == HttpStatusCode.OK, firstQuestion.Body.ToString());
        Assert.True(secondQuestion.Status == HttpStatusCode.OK, secondQuestion.Body.ToString());
        var paper = await Http.Post(scenario.Admin, "/api/exam-papers",
            new { name = "增量卷" + suffix, description = "", remark = "" }, scenario.AdminCsrf);
        Assert.True(paper.Status == HttpStatusCode.OK, paper.Body.ToString());
        var paperId = paper.Body.GetProperty("id").GetString()!;
        var added = await Http.Post(scenario.Admin, $"/api/exam-papers/{paperId}/questions", new
        {
            items = new[]
            {
                new { questionId = firstQuestion.Body.GetProperty("id").GetString() },
                new { questionId = secondQuestion.Body.GetProperty("id").GetString() }
            }
        }, scenario.AdminCsrf);
        Assert.True(added.Status == HttpStatusCode.OK, added.Body.ToString());

        var now = DateTime.UtcNow;
        var exam = await Http.Post(scenario.Admin, "/api/exams", new
        {
            name = "增量考" + suffix, paperId, description = "",
            startAt = now.AddMinutes(-5), endAt = now.AddHours(2), durationMinutes = 30
        }, scenario.AdminCsrf);
        Assert.True(exam.Status == HttpStatusCode.OK, exam.Body.ToString());
        var examId = exam.Body.GetProperty("id").GetString()!;
        var enrolled = await Http.Post(scenario.Admin, $"/api/exams/{examId}/students",
            new { studentUserIds = new[] { userId } }, scenario.AdminCsrf);
        Assert.True(enrolled.Status == HttpStatusCode.OK, enrolled.Body.ToString());
        var opened = await Http.Post(scenario.Admin, $"/api/exams/{examId}/open", new { }, scenario.AdminCsrf);
        Assert.True(opened.Status == HttpStatusCode.OK, opened.Body.ToString());
        var started = await Http.Post(student, $"/api/exam-hall/{examId}/start", new { }, studentCsrf);
        Assert.True(started.Status == HttpStatusCode.OK, started.Body.ToString());

        var hall = await Http.Get(student, $"/api/exam-hall/{examId}");
        Assert.True(hall.Status == HttpStatusCode.OK, hall.Body.ToString());
        var questions = hall.Body.GetProperty("questions").EnumerateArray().ToArray();
        var firstId = questions.Single(item => item.GetProperty("stem").GetString() == "第一题" + suffix)
            .GetProperty("paperQuestionId").GetString()!;
        var secondId = questions.Single(item => item.GetProperty("stem").GetString() == "第二题" + suffix)
            .GetProperty("paperQuestionId").GetString()!;
        var baseline = hall.Body.GetProperty("serverNow").GetString();

        var firstSave = await Save(student, studentCsrf, examId, baseline, firstId, 0);
        Assert.Equal(1, firstSave.Body.GetProperty("saved").GetInt32());
        var unchanged = await Save(student, studentCsrf, examId, firstSave.Body.GetProperty("serverNow").GetString(), firstId, 0);
        Assert.Equal(0, unchanged.Body.GetProperty("saved").GetInt32());
        var secondSave = await Save(student, studentCsrf, examId, firstSave.Body.GetProperty("serverNow").GetString(), secondId, 1);
        Assert.True(secondSave.Status == HttpStatusCode.OK, secondSave.Body.ToString());
        Assert.Equal(1, secondSave.Body.GetProperty("saved").GetInt32());

        var again = await Http.Get(student, $"/api/exam-hall/{examId}");
        var stored = again.Body.GetProperty("questions").EnumerateArray().ToDictionary(
            item => item.GetProperty("paperQuestionId").GetString()!,
            item => item.GetProperty("selected").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal([0], stored[firstId]);
        Assert.Equal([1], stored[secondId]);
    }

    static async Task<ApiResponse> Save(HttpClient client, string csrf, string examId, string? loadedAt, string paperQuestionId, int selected)
        => await Http.Post(client, $"/api/exam-hall/{examId}/answers", new
        {
            answers = new[] { new { paperQuestionId, selected = new[] { selected }, text = "" } },
            loadedAt
        }, csrf);

    static async Task<string> ChangePassword(HttpClient client, string userName, string currentPassword, string newPassword)
    {
        var login = await Http.Post(client, "/api/auth/login", new { userName, password = currentPassword });
        Assert.True(login.Status == HttpStatusCode.OK, login.Body.ToString());
        var csrf = login.Body.GetProperty("csrfToken").GetString()!;
        var changed = await Http.Post(client, "/api/auth/password", new { currentPassword, newPassword }, csrf);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body.ToString());
        return changed.Body.GetProperty("csrfToken").GetString()!;
    }

    static async Task<string> SignIn(HttpClient client, Dictionary<string, string> jar, string userName, string password)
    {
        using var response = await Http.PostRaw(client, "/api/auth/login", new { userName, password });
        var body = await Read(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        Merge(jar, response);
        return body.GetProperty("csrfToken").GetString()!;
    }

    static async Task<string> ChangePassword(HttpClient client, Dictionary<string, string> jar, string csrf, string currentPassword, string newPassword)
    {
        var changed = await Send(client, jar, HttpMethod.Post, "/api/auth/password", new { currentPassword, newPassword }, csrf);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body.ToString());
        return changed.Body.GetProperty("csrfToken").GetString()!;
    }

    static async Task<ApiResponse> Get(HttpClient client, Dictionary<string, string> jar, string path)
        => await Send(client, jar, HttpMethod.Get, path, null, null);

    static async Task<ApiResponse> Send(HttpClient client, Dictionary<string, string> jar, HttpMethod method, string path, object? body, string? csrf)
    {
        var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (csrf != null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (jar.Count > 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", jar.Select(item => $"{item.Key}={item.Value}")));
        using var response = await client.SendAsync(request);
        var payload = await Read(response);
        if (response.Headers.Contains("Set-Cookie")) Merge(jar, response);
        return new ApiResponse(response.StatusCode, payload);
    }

    static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return default;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    static void Merge(Dictionary<string, string> jar, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var value in values)
        {
            var pair = value.Split(';')[0];
            var split = pair.IndexOf('=');
            if (split <= 0) continue;
            var cookie = pair[..split];
            var cookieValue = pair[(split + 1)..];
            if (cookieValue.Length == 0) jar.Remove(cookie);
            else jar[cookie] = cookieValue;
        }
    }
}
