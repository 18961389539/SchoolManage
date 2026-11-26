using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using School.Training;

namespace School.Training.Tests;

[Collection("http")]
public sealed class LiveShareTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Frames_flow_only_while_a_teacher_is_watching()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var account = "lv" + suffix;
        var created = await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "student",
            name = "画面学生",
            userName = account,
            number = "L" + suffix,
            role = "Student",
            password = "StudentPass2026"
        }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var personId = created.Body.GetProperty("id").GetString()!;
        var studentCookies = await LoginAsync(account, "StudentPass2026", "StudentPass2026x");
        var adminCookies = await LoginAsync("admin", ApiScenario.AdminPassword, null);

        string sessionId = "";
        await scenario.WithDb(async db =>
        {
            var adminId = await db.Users.Where(user => user.UserName == "admin").Select(user => user.Id).SingleAsync();
            var studentId = await db.Users.Where(user => user.UserName == account).Select(user => user.Id).SingleAsync();
            var course = new TrainingCourse { Name = "画面课" + suffix, Type = "general", Status = "published" };
            var chapter = new CourseChapter { CourseId = course.Id, Name = "章" };
            var section = new CourseSection { ChapterId = chapter.Id, Name = "节" };
            var resource = new CourseResource { SectionId = section.Id, Name = "实操", Kind = "casePractice" };
            var room = new Classroom
            {
                Name = "画面课堂" + suffix,
                CourseId = course.Id,
                TeacherUserId = adminId,
                OpenAt = DateTime.UtcNow.AddHours(-1),
                CloseAt = DateTime.UtcNow.AddHours(4),
                Status = "open"
            };
            var record = new LearningRecord { ClassroomId = room.Id, UserId = studentId, ResourceId = resource.Id, Status = "inProgress" };
            var session = new ClassroomCaseSession
            {
                ClassroomId = room.Id,
                LearningRecordId = record.Id,
                UserId = studentId,
                ResourceId = resource.Id,
                Status = "active",
                WorkId = "work" + suffix
            };
            db.AddRange(course, chapter, section, resource, room,
                new ClassroomStudent { ClassroomId = room.Id, StudentUserId = studentId, StudentPersonId = personId },
                record, session);
            await db.SaveChangesAsync();
            sessionId = session.Id;
        });

        var idle = await Http.Get(scenario.Admin, "/api/classrooms/case-sessions/" + sessionId + "/work");
        Assert.Equal(HttpStatusCode.OK, idle.Status);
        Assert.False(idle.Body.GetProperty("allowAssist").GetBoolean());

        using var teacher = await ConnectAsync(adminCookies, sessionId);
        var waiting = await ReceiveAsync(teacher, CancellationToken.None);
        Assert.Contains("\"sharing\":false", Encoding.UTF8.GetString(waiting.Payload), StringComparison.Ordinal);

        using var student = await ConnectAsync(studentCookies, sessionId);
        await student.SendAsync(Encoding.UTF8.GetBytes("""{"allowAssist":true}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var started = await ReceiveAsync(student, CancellationToken.None);
        var status = await ReceiveAsync(teacher, CancellationToken.None);
        var watching = await Http.Get(scenario.Admin, "/api/classrooms/case-sessions/" + sessionId + "/work");
        Assert.Equal(HttpStatusCode.OK, watching.Status);
        Assert.True(watching.Body.GetProperty("allowAssist").GetBoolean());
        Assert.Equal(WebSocketMessageType.Text, started.Type);
        Assert.Contains("\"type\":\"start\"", Encoding.UTF8.GetString(started.Payload), StringComparison.Ordinal);
        Assert.Equal(WebSocketMessageType.Text, status.Type);
        var teacherStatus = Encoding.UTF8.GetString(status.Payload);
        Assert.Contains("\"sharing\":true", teacherStatus, StringComparison.Ordinal);
        Assert.Contains("\"allowAssist\":true", teacherStatus, StringComparison.Ordinal);

        var jpeg = new byte[] { 0xff, 0xd8, 0x00, 0xff, 0xd9 };
        await student.SendAsync(jpeg, WebSocketMessageType.Binary, true, CancellationToken.None);
        var frame = await ReceiveAsync(teacher, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Binary, frame.Type);
        Assert.Equal(jpeg, frame.Payload);

        await teacher.CloseAsync(WebSocketCloseStatus.NormalClosure, "离开", CancellationToken.None);
        var paused = await ReceiveAsync(student, CancellationToken.None);
        Assert.Contains("\"type\":\"stop\"", Encoding.UTF8.GetString(paused.Payload), StringComparison.Ordinal);

        await student.CloseAsync(WebSocketCloseStatus.NormalClosure, "停止", CancellationToken.None);
        var ended = await Http.Get(scenario.Admin, "/api/classrooms/case-sessions/" + sessionId + "/work");
        Assert.Equal(HttpStatusCode.OK, ended.Status);
        Assert.False(ended.Body.GetProperty("allowAssist").GetBoolean());
    }

    async Task<WebSocket> ConnectAsync(string cookies, string sessionId)
    {
        var client = scenario.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Cookie"] = cookies;
            request.Headers["Origin"] = $"{request.Scheme}://{request.Host}";
        };
        return await client.ConnectAsync(
            new Uri(scenario.Factory.Server.BaseAddress!, "api/classrooms/case-sessions/" + sessionId + "/live/socket"),
            CancellationToken.None);
    }

        static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[ClassroomLiveShare.MaxFrameLength + 1];
        var count = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(count), timeout.Token);
            count += result.Count;
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("共享连接已关闭");
        } while (!result.EndOfMessage);
        return (result.MessageType, buffer[..count]);
    }

    async Task<string> LoginAsync(string userName, string password, string? replacement)
    {
        using var client = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        var login = await SendAsync(client, HttpMethod.Post, "/api/auth/login", new { userName, password }, "");
        Assert.Equal(HttpStatusCode.OK, login.Response.StatusCode);
        var cookies = login.Cookies;
        if (replacement == null) return cookies;
        using var body = await JsonDocument.ParseAsync(await login.Response.Content.ReadAsStreamAsync());
        var csrf = body.RootElement.GetProperty("csrfToken").GetString()!;
        var changed = await SendAsync(client, HttpMethod.Post, "/api/auth/password",
            new { currentPassword = password, newPassword = replacement }, cookies, csrf);
        Assert.Equal(HttpStatusCode.OK, changed.Response.StatusCode);
        return changed.Cookies;
    }

    static async Task<(HttpResponseMessage Response, string Cookies)> SendAsync(HttpClient client, HttpMethod method, string path, object body, string cookies, string? csrf = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (csrf != null) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        var response = await client.SendAsync(request);
        return (response, MergeCookies(cookies, response));
    }

    static string MergeCookies(string current, HttpResponseMessage response)
    {
        var jar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in current.Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf('=');
            if (split > 0) jar[part[..split]] = part;
        }
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                var pair = value.Split(';', 2)[0];
                var split = pair.IndexOf('=');
                if (split > 0) jar[pair[..split]] = pair;
            }
        }
        return string.Join("; ", jar.Values);
    }
}
