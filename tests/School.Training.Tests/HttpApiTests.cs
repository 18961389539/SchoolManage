using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using School.Training;

namespace School.Training.Tests;

[CollectionDefinition("http", DisableParallelization = true)]
public sealed class HttpCollection { }

/// <summary>
/// Hosts the real application over an in-memory transport with a dedicated data directory.
/// The connection filter makes the request look loopback (or deliberately not), because the
/// TestServer has no real socket and the first-run setup deliberately requires a local client.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    readonly string dataDir = Path.Combine(Path.GetTempPath(), "school-http-" + Guid.NewGuid().ToString("N"));
    readonly string? previousDataDir;
    readonly string? previousAutomaticBackups;
    readonly IPAddress clientAddress;
    readonly bool demo;
    readonly IReadOnlyDictionary<string, string?> settings;
    readonly Action<IServiceCollection>? configureServices;

    public ApiFactory(IPAddress? remoteAddress = null, bool demo = false, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null)
    {
        this.demo = demo;
        this.settings = settings ?? new Dictionary<string, string?>();
        this.configureServices = configureServices;
        clientAddress = remoteAddress ?? IPAddress.Loopback;
        previousDataDir = Environment.GetEnvironmentVariable("SCHOOL_DATA_DIR");
        previousAutomaticBackups = Environment.GetEnvironmentVariable("School__Backups__Automatic__Enabled");
        Environment.SetEnvironmentVariable("SCHOOL_DATA_DIR", dataDir);
        Environment.SetEnvironmentVariable("School__Backups__Automatic__Enabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("School:Demo", demo ? "1" : "0");
        foreach (var setting in settings)
            builder.UseSetting(setting.Key, setting.Value);
        builder.UseEnvironment("Development");
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "..", "..", "..")));
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new ConnectionInfoStartupFilter(clientAddress)));
        if (configureServices != null) builder.ConfigureTestServices(configureServices);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Environment.SetEnvironmentVariable("SCHOOL_DATA_DIR", previousDataDir);
        Environment.SetEnvironmentVariable("School__Backups__Automatic__Enabled", previousAutomaticBackups);
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

sealed class ConnectionInfoStartupFilter(IPAddress remote) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, nextMiddleware) =>
        {
            ctx.Connection.RemoteIpAddress = remote;
            ctx.Connection.LocalIpAddress = IPAddress.Loopback;
            return nextMiddleware();
        });
        next(app);
    };
}

public readonly record struct ApiResponse(HttpStatusCode Status, JsonElement Body)
{
    public string Message => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("message", out var message)
        ? message.GetString() ?? ""
        : "";
}

public static class Http
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static Task<ApiResponse> Get(HttpClient client, string path)
        => Send(client, new HttpRequestMessage(HttpMethod.Get, path));

    public static Task<ApiResponse> Post(HttpClient client, string path, object body, string? csrf = null, string? origin = null)
        => Send(client, Request(HttpMethod.Post, path, body, csrf, origin));

    public static Task<ApiResponse> Post(HttpClient client, string path, object body, Action<HttpRequestMessage> configure)
    {
        var request = Request(HttpMethod.Post, path, body, null, null);
        configure(request);
        return Send(client, request);
    }

    public static Task<ApiResponse> Put(HttpClient client, string path, object body, string? csrf = null, string? origin = null)
        => Send(client, Request(HttpMethod.Put, path, body, csrf, origin));

    public static Task<ApiResponse> Delete(HttpClient client, string path, string? csrf = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        if (csrf != null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (origin != null) request.Headers.Add("Origin", origin);
        return Send(client, request);
    }

    static HttpRequestMessage Request(HttpMethod method, string path, object body, string? csrf, string? origin)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: Options) };
        if (csrf != null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (origin != null) request.Headers.Add("Origin", origin);
        return request;
    }

    public static async Task<HttpResponseMessage> PostRaw(HttpClient client, string path, object body, string? csrf = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Options) };
        if (csrf != null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    static async Task<ApiResponse> Send(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var body = default(JsonElement);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var document = JsonDocument.Parse(text);
                body = document.RootElement.Clone();
            }
            return new ApiResponse(response.StatusCode, body);
        }
    }
}

public sealed class ApiScenario : IAsyncLifetime
{
    public const string AdminPassword = "AdminPassw0rd2026";
    public const string StudentPassword = "StudentPass2026x";

    public ApiFactory Factory { get; private set; } = null!;
    public HttpClient Anonymous { get; private set; } = null!;
    public HttpClient Admin { get; private set; } = null!;
    public string AdminCsrf { get; private set; } = "";
    public HttpClient Student { get; private set; } = null!;
    public string StudentCsrf { get; private set; } = "";

    public async Task InitializeAsync()
    {
        Factory = new ApiFactory();
        Anonymous = Client();
        Admin = Client();
        var setup = await Http.Post(Admin, "/api/setup", new
        {
            schoolName = "接口测试学校",
            userName = "admin",
            displayName = "管理员",
            password = AdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, setup.Status);
        AdminCsrf = setup.Body.GetProperty("csrfToken").GetString()!;

        var created = await Http.Post(Admin, "/api/people", new
        {
            kind = "student",
            name = "接口测试学生",
            userName = "student01",
            number = "S001",
            role = "Student",
            password = "StudentPass2026"
        }, AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);

        Student = Client();
        var login = await Http.Post(Student, "/api/auth/login", new { userName = "student01", password = "StudentPass2026" });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var csrf = login.Body.GetProperty("csrfToken").GetString()!;
        var changed = await Http.Post(Student, "/api/auth/password",
            new { currentPassword = "StudentPass2026", newPassword = StudentPassword }, csrf);
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        StudentCsrf = changed.Body.GetProperty("csrfToken").GetString()!;
    }

    HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task WithDb(Func<SchoolDbContext, Task> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<SchoolDbContext>());
    }

    public Task DisposeAsync()
    {
        Anonymous.Dispose();
        Admin.Dispose();
        Student.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

[Collection("http")]
public sealed class HttpApiTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Anonymous_api_request_returns_json_401()
    {
        var response = await Http.Get(scenario.Anonymous, "/api/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Contains("登录", response.Message);
    }

    [Fact]
    public async Task Healthz_reports_database_and_disk_without_a_session()
    {
        var response = await Http.Get(scenario.Anonymous, "/healthz");
        Assert.True(response.Status is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
        Assert.Equal("ok", response.Body.GetProperty("checks").GetProperty("database").GetString());
        Assert.Contains(response.Body.GetProperty("checks").GetProperty("disk").GetString(), new[] { "ok", "low", "unavailable" });
    }

    [Fact]
    public async Task Settings_reports_a_configured_backup_directory()
    {
        var backupDirectory = Directory.CreateTempSubdirectory("school-backup-config-");
        var previous = Environment.GetEnvironmentVariable("School__Backups__Directory");
        Environment.SetEnvironmentVariable("School__Backups__Directory", backupDirectory.FullName);
        try
        {
            using var factory = new ApiFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var setup = await Http.Post(client, "/api/setup", new
            {
                schoolName = "备份目录测试学校",
                userName = "backupadmin",
                displayName = "管理员",
                password = ApiScenario.AdminPassword
            });
            Assert.Equal(HttpStatusCode.OK, setup.Status);

            var settings = await Http.Get(client, "/api/settings");
            Assert.Equal(HttpStatusCode.OK, settings.Status);
            Assert.Equal(Path.GetFullPath(backupDirectory.FullName), settings.Body.GetProperty("backupDirectory").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("School__Backups__Directory", previous);
            try { backupDirectory.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Setup_requires_a_loopback_client()
    {
        using var factory = new ApiFactory(IPAddress.Parse("203.0.113.9"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await Http.Post(client, "/api/setup", new
        {
            schoolName = "外部学校",
            userName = "admin",
            displayName = "管理员",
            password = ApiScenario.AdminPassword
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Contains("本机", response.Message);
    }

    [Fact]
    public async Task Login_sets_an_http_only_strict_cookie()
    {
        using var client = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await Http.PostRaw(client, "/api/auth/login",
            new { userName = "admin", password = ApiScenario.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join(";", values) : "";
        Assert.Contains("SchoolTraining.Session=", cookies);
        Assert.Contains("httponly", cookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookies, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mutating_request_without_csrf_is_rejected()
    {
        var response = await Http.Post(scenario.Admin, "/api/professions",
            new { name = "无令牌专业", code = "NO-CSRF", remark = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains("安全令牌", response.Message);
    }

    [Fact]
    public async Task Cross_site_origin_is_rejected_before_the_endpoint_runs()
    {
        var response = await Http.Post(scenario.Admin, "/api/professions",
            new { name = "跨站专业", code = "CROSS-SITE", remark = "" },
            scenario.AdminCsrf, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Contains("跨站", response.Message);
    }

    [Fact]
    public async Task Student_cannot_reach_admin_endpoints()
    {
        var audits = await Http.Get(scenario.Student, "/api/audits");
        Assert.Equal(HttpStatusCode.Forbidden, audits.Status);
        var settings = await Http.Put(scenario.Student, "/api/settings", new { schoolName = "越权改名" }, scenario.StudentCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, settings.Status);
        var dashboard = await Http.Get(scenario.Student, "/api/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.Status);
    }

    [Fact]
    public async Task Admin_crud_works_with_a_valid_csrf_token()
    {
        var list = await Http.Get(scenario.Admin, "/api/professions");
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.True(list.Body.TryGetProperty("items", out var items));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);

        var created = await Http.Post(scenario.Admin, "/api/professions",
            new { name = "接口测试专业", code = "HTTP-P1", remark = "集成测试" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("接口测试专业", created.Body.GetProperty("name").GetString());

        var search = await Http.Get(scenario.Admin, "/api/professions?search=HTTP-P1");
        Assert.Equal(HttpStatusCode.OK, search.Status);
        Assert.Equal(1, search.Body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Course_content_with_learning_history_cannot_be_deleted()
    {
        var course = await Http.Post(scenario.Admin, "/api/courses",
            new { name = "删除保护课程", type = "general" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, course.Status);
        var courseId = course.Body.GetProperty("id").GetString()!;

        var chapter = await Http.Post(scenario.Admin, $"/api/courses/{courseId}/chapters",
            new { name = "删除保护大章" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, chapter.Status);
        var chapterId = chapter.Body.GetProperty("id").GetString()!;

        var section = await Http.Post(scenario.Admin, $"/api/chapters/{chapterId}/sections",
            new { name = "删除保护小节", durationMinutes = 30 }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, section.Status);
        var sectionId = section.Body.GetProperty("id").GetString()!;

        var used = await Http.Post(scenario.Admin, $"/api/sections/{sectionId}/resources",
            new { name = "已有学习记录的资源", kind = "theory", durationMinutes = 15, content = "正文" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, used.Status);
        var usedId = used.Body.GetProperty("id").GetString()!;

        var unused = await Http.Post(scenario.Admin, $"/api/sections/{sectionId}/resources",
            new { name = "没有学习记录的资源", kind = "theory", durationMinutes = 15, content = "正文" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, unused.Status);
        var unusedId = unused.Body.GetProperty("id").GetString()!;

        await scenario.WithDb(async db =>
        {
            var adminId = await db.Users.Where(x => x.UserName == "admin").Select(x => x.Id).SingleAsync();
            var studentId = await db.Users.Where(x => x.UserName == "student01").Select(x => x.Id).SingleAsync();
            var classroom = new Classroom
            {
                Name = "删除保护课堂", CourseId = courseId, TeacherUserId = adminId, Status = "closed",
                OpenAt = DateTime.UtcNow.AddHours(-2), CloseAt = DateTime.UtcNow.AddHours(-1)
            };
            db.Classrooms.Add(classroom);
            db.LearningRecords.Add(new LearningRecord { ClassroomId = classroom.Id, UserId = studentId, ResourceId = usedId });
            await db.SaveChangesAsync();
        });

        var blockedResource = await Http.Delete(scenario.Admin, $"/api/resources/{usedId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, blockedResource.Status);
        Assert.Contains("学习记录", blockedResource.Message);

        var blockedSection = await Http.Delete(scenario.Admin, $"/api/sections/{sectionId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, blockedSection.Status);

        var blockedChapter = await Http.Delete(scenario.Admin, $"/api/chapters/{chapterId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, blockedChapter.Status);

        Assert.Equal(HttpStatusCode.OK, (await Http.Get(scenario.Admin, $"/api/resources/{usedId}")).Status);
        Assert.Equal(HttpStatusCode.OK,
            (await Http.Delete(scenario.Admin, $"/api/resources/{unusedId}", scenario.AdminCsrf)).Status);
    }

    [Fact]
    public async Task Classroom_deletion_requires_closed_state_and_no_history()
    {
        string blockedId = "", openId = "", removableId = "";
        await scenario.WithDb(async db =>
        {
            var admin = await db.Users.SingleAsync(x => x.UserName == "admin");
            var student = await db.Users.SingleAsync(x => x.UserName == "student01");
            var course = new TrainingCourse { Name = "课堂删除保护课程", Type = "general", Status = "published" };
            var chapter = new CourseChapter { CourseId = course.Id, Name = "第一章", SortOrder = 1 };
            var section = new CourseSection { ChapterId = chapter.Id, Name = "第一节", SortOrder = 1 };
            var resource = new CourseResource { SectionId = section.Id, Name = "理论资源", Kind = "theory", Content = "正文", SortOrder = 1 };
            var blocked = new Classroom
            {
                Name = "有历史课堂", CourseId = course.Id, TeacherUserId = admin.Id, Status = "closed",
                OpenAt = DateTime.UtcNow.AddHours(-2), CloseAt = DateTime.UtcNow.AddHours(-1)
            };
            var open = new Classroom
            {
                Name = "开放中课堂", CourseId = course.Id, TeacherUserId = admin.Id, Status = "open",
                OpenAt = DateTime.UtcNow.AddHours(-1), CloseAt = DateTime.UtcNow.AddHours(1)
            };
            var removable = new Classroom
            {
                Name = "无历史课堂", CourseId = course.Id, TeacherUserId = admin.Id, Status = "scheduled",
                OpenAt = DateTime.UtcNow.AddHours(1), CloseAt = DateTime.UtcNow.AddHours(2)
            };
            db.AddRange(course, chapter, section, resource, blocked, open, removable);
            db.ClassroomStudents.Add(new ClassroomStudent { ClassroomId = blocked.Id, StudentUserId = student.Id });
            db.LearningRecords.Add(new LearningRecord
            {
                ClassroomId = blocked.Id, UserId = student.Id, ResourceId = resource.Id, Status = "completed"
            });
            await db.SaveChangesAsync();
            blockedId = blocked.Id;
            openId = open.Id;
            removableId = removable.Id;
        });

        var openBlocked = await Http.Delete(scenario.Admin, $"/api/classrooms/{openId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, openBlocked.Status);
        Assert.Contains("先结课", openBlocked.Message);

        var historyBlocked = await Http.Delete(scenario.Admin, $"/api/classrooms/{blockedId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Conflict, historyBlocked.Status);
        Assert.Contains("历史记录", historyBlocked.Message);

        var removed = await Http.Delete(scenario.Admin, $"/api/classrooms/{removableId}", scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, removed.Status);

        await scenario.WithDb(async db =>
        {
            Assert.NotNull(await db.Classrooms.FindAsync(blockedId));
            Assert.NotNull(await db.Classrooms.FindAsync(openId));
            Assert.Null(await db.Classrooms.FindAsync(removableId));
            Assert.Equal(1, await db.LearningRecords.CountAsync(x => x.ClassroomId == blockedId));
        });
    }

    [Fact]
    public async Task Diagnostics_zip_writer_produces_a_readable_archive()
    {
        using var buffer = new MemoryStream();
        var snapshot = new DiagnosticsSnapshot(DateTime.UtcNow, new { ok = true }, new { schoolName = "测试" },
            new { machineName = "test" }, new { counts = 1 }, Array.Empty<string>());
        await DiagnosticsBundle.WriteAsync(buffer, snapshot, CancellationToken.None);
        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.NotNull(archive.GetEntry("status.json"));
    }

    [Fact]
    public async Task Diagnostics_bundle_is_admin_only_and_contains_no_secrets()
    {
        var student = await Http.Get(scenario.Student, "/api/diagnostics");
        Assert.Equal(HttpStatusCode.Forbidden, student.Status);

        using var response = await scenario.Admin.GetAsync("/api/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        foreach (var name in new[] { "README.txt", "status.json", "settings.json", "system.json", "database.json", "manifest.json" })
            Assert.NotNull(archive.GetEntry(name));
        Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("keys/", StringComparison.Ordinal) || entry.Name.EndsWith(".db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audit_export_is_admin_only_and_escapes_formula_cells()
    {
        var student = await Http.Get(scenario.Student, "/api/audits/export");
        Assert.Equal(HttpStatusCode.Forbidden, student.Status);

        var created = await Http.Post(scenario.Admin, "/api/professions",
            new { name = "=1+1", code = "CSV-1", remark = "" }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);

        using var response = await scenario.Admin.GetAsync("/api/audits/export?search=" + Uri.EscapeDataString("=1+1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("时间（北京时间）,账号,操作,对象,详情", csv);
        Assert.Contains("'=1+1", csv);
        Assert.Contains("新增专业", csv);
        Assert.Contains("导出操作日志", csv);
    }

    [Fact]
    public async Task Must_change_password_blocks_business_apis_until_the_password_changes()
    {
        var created = await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "student",
            name = "首次登录学生",
            userName = "http-must-01",
            number = "HM001",
            role = "Student",
            password = "InitialPass2026"
        }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);

        using var client = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Http.Post(client, "/api/auth/login", new { userName = "http-must-01", password = "InitialPass2026" });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.True(login.Body.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        var csrf = login.Body.GetProperty("csrfToken").GetString()!;

        var blocked = await Http.Get(client, "/api/dashboard");
        Assert.Equal((HttpStatusCode)428, blocked.Status);
        var me = await Http.Get(client, "/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.Status);

        var changed = await Http.Post(client, "/api/auth/password",
            new { currentPassword = "InitialPass2026", newPassword = "ChangedPass2026" }, csrf);
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        var allowed = await Http.Get(client, "/api/dashboard");
        Assert.Equal(HttpStatusCode.OK, allowed.Status);
    }

    [Fact]
    public async Task Logout_revokes_the_current_session()
    {
        var created = await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "student",
            name = "退出登录学生",
            userName = "logout01",
            number = "LO001",
            role = "Student",
            password = "LogoutPass2026"
        }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);

        using var client = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Http.Post(client, "/api/auth/login", new { userName = "logout01", password = "LogoutPass2026" });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var csrf = login.Body.GetProperty("csrfToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Http.Get(client, "/api/auth/me")).Status);

        var logout = await Http.Post(client, "/api/auth/logout", new { }, csrf);
        Assert.Equal(HttpStatusCode.OK, logout.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.Get(client, "/api/auth/me")).Status);
    }

    [Fact]
    public async Task Legacy_password_hash_is_upgraded_after_a_successful_login()
    {
        var legacyHasher = new PasswordHasher<AppUser>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 }));
        var legacy = new AppUser { UserName = "legacy01", DisplayName = "历史账号", Role = "Teacher" };
        legacy.PasswordHash = legacyHasher.HashPassword(legacy, "LegacyPass2026");
        var originalHash = legacy.PasswordHash;
        await scenario.WithDb(async db => { db.Users.Add(legacy); await db.SaveChangesAsync(); });

        using var client = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Http.Post(client, "/api/auth/login", new { userName = "legacy01", password = "LegacyPass2026" });
        Assert.Equal(HttpStatusCode.OK, login.Status);

        string stored = "";
        await scenario.WithDb(async db =>
            stored = await db.Users.Where(x => x.Id == legacy.Id).Select(x => x.PasswordHash).SingleAsync(CancellationToken.None));
        Assert.NotEqual(originalHash, stored);
        var currentHasher = new PasswordHasher<AppUser>(Options.Create(new PasswordHasherOptions { IterationCount = 600000 }));
        Assert.Equal(PasswordVerificationResult.Success,
            currentHasher.VerifyHashedPassword(legacy, stored, "LegacyPass2026"));
    }

    [Fact]
    public async Task Backups_list_reports_the_automatic_schedule()
    {
        var response = await Http.Get(scenario.Admin, "/api/backups");
        Assert.Equal(HttpStatusCode.OK, response.Status);
        var automatic = response.Body.GetProperty("automatic");
        Assert.False(automatic.GetProperty("enabled").GetBoolean());
        Assert.Equal("02:30", automatic.GetProperty("timeOfDay").GetString());
        Assert.Equal(JsonValueKind.String, automatic.GetProperty("lastMessage").ValueKind);
        Assert.Equal("dpapi", response.Body.GetProperty("keyProtection").GetProperty("mode").GetString());
        Assert.False(response.Body.GetProperty("keyProtection").GetProperty("portable").GetBoolean());
    }

    [Fact]
    public async Task Backup_inspection_reports_machine_bound_keys_and_does_not_restore()
    {
        var created = await Http.Post(scenario.Admin, "/api/backups", new { }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var fileName = created.Body.GetProperty("fileName").GetString()!;
        var report = await Http.Post(scenario.Admin, "/api/backups/" + Uri.EscapeDataString(fileName) + "/inspect", new { }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, report.Status);
        Assert.True(report.Body.GetProperty("ok").GetBoolean());
        Assert.False(report.Body.GetProperty("portable").GetBoolean());
        Assert.Equal("dpapi", report.Body.GetProperty("keyProtection").GetString());
        var stillThere = await Http.Get(scenario.Admin, "/api/status");
        Assert.Equal("接口测试学校", stillThere.Body.GetProperty("schoolName").GetString());
    }

    [Fact]
    public async Task Uploaded_backup_inspection_rejects_a_file_that_is_not_a_backup()
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent("not a zip"u8.ToArray());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "notes.zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/backups/inspect") { Content = content };
        request.Headers.Add("X-CSRF-TOKEN", scenario.AdminCsrf);
        using var response = await scenario.Admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不是有效的 ZIP", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Recovery_passphrase_makes_the_backup_inspection_portable()
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string?>
        {
            ["School:DataProtection:RecoveryPassphrase"] = "RecoverPass2026"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setup = await Http.Post(client, "/api/setup", new
        {
            schoolName = "可携带密钥学校",
            userName = "admin",
            displayName = "管理员",
            password = ApiScenario.AdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, setup.Status);
        var csrf = setup.Body.GetProperty("csrfToken").GetString()!;
        var created = await Http.Post(client, "/api/backups", new { }, csrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var fileName = created.Body.GetProperty("fileName").GetString()!;
        var report = await Http.Post(client, "/api/backups/" + Uri.EscapeDataString(fileName) + "/inspect", new { }, csrf);
        Assert.Equal(HttpStatusCode.OK, report.Status);
        Assert.True(report.Body.GetProperty("ok").GetBoolean());
        Assert.True(report.Body.GetProperty("portable").GetBoolean());
        Assert.Equal("passphrase", report.Body.GetProperty("keyProtection").GetString());
    }

    [Fact]
    public async Task Unknown_api_path_returns_json_404()
    {
        var response = await Http.Get(scenario.Anonymous, "/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Contains("接口不存在", response.Message);
    }

    [Fact]
    public async Task Concurrent_completions_share_one_learning_record()
    {
        string classroomId = "", resourceId = "", studentId = "";
        await scenario.WithDb(async db =>
        {
            var admin = await db.Users.SingleAsync(x => x.UserName == "admin");
            var student = await db.Users.SingleAsync(x => x.UserName == "student01");
            var course = new TrainingCourse { Name = "并发课程", Type = "general", Status = "published" };
            var chapter = new CourseChapter { CourseId = course.Id, Name = "第一章", SortOrder = 1 };
            var section = new CourseSection { ChapterId = chapter.Id, Name = "第一节", SortOrder = 1 };
            var resource = new CourseResource { SectionId = section.Id, Name = "理论资源", Kind = "theory", Content = "正文", SortOrder = 1 };
            var classroom = new Classroom
            {
                CourseId = course.Id, Name = "并发课堂", TeacherUserId = admin.Id, Status = "open",
                OpenAt = DateTime.UtcNow.AddHours(-1), CloseAt = DateTime.UtcNow.AddHours(1)
            };
            db.AddRange(course, chapter, section, resource, classroom);
            db.ClassroomStudents.Add(new ClassroomStudent { ClassroomId = classroom.Id, StudentUserId = student.Id });
            await db.SaveChangesAsync();
            classroomId = classroom.Id;
            resourceId = resource.Id;
            studentId = student.Id;
        });

        // Hold the write lock while both requests pass their validation reads and reach the
        // record insert, so the duplicate attempt is forced rather than left to timing.
        await using var scope = scenario.Factory.Services.CreateAsyncScope();
        var lockDb = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        await using var blocker = await lockDb.Database.BeginTransactionAsync();
        var locked = await lockDb.Classrooms.SingleAsync(x => x.Id == classroomId);
        locked.Name = "并发课堂（占用写锁）";
        await lockDb.SaveChangesAsync();

        var path = $"/api/classrooms/{classroomId}/resources/{resourceId}/complete";
        var first = Http.Post(scenario.Student, path, new { }, scenario.StudentCsrf);
        var second = Http.Post(scenario.Student, path, new { }, scenario.StudentCsrf);
        await Task.Delay(2000);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await blocker.CommitAsync();
        var responses = await Task.WhenAll(first, second);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.Status));
        await scenario.WithDb(async db =>
            Assert.Equal(1, await db.LearningRecords.CountAsync(x =>
                x.ClassroomId == classroomId && x.UserId == studentId && x.ResourceId == resourceId)));
    }

    [Fact]
    public async Task Login_locks_one_account_after_eight_failures_and_leaves_other_accounts_open()
    {
        using var factory = new ApiFactory(IPAddress.Parse("198.51.100.7"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var response = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        }
        var limited = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Contains("失败次数过多", limited.Message);
        var other = await Http.Post(client, "/api/auth/login", new { userName = "someone", password = "WrongPass2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, other.Status);
    }

    [Fact]
    public async Task Login_ip_burst_limit_is_independent_of_the_account()
    {
        using var factory = new ApiFactory(IPAddress.Parse("198.51.100.8"), settings: new Dictionary<string, string?>
        {
            ["School:Login:IpPermitLimit"] = "3"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await Http.Post(client, "/api/auth/login", new { userName = "user" + attempt, password = "WrongPass2026" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        }
        var limited = await Http.Post(client, "/api/auth/login", new { userName = "user-extra", password = "WrongPass2026" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Contains("过于频繁", limited.Message);
    }

    [Fact]
    public async Task Successful_login_clears_the_account_failure_streak()
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string?>
        {
            ["School:Login:FailureLimit"] = "2"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setup = await Http.Post(client, "/api/setup", new
        {
            schoolName = "限流测试学校",
            userName = "admin",
            displayName = "管理员",
            password = ApiScenario.AdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, setup.Status);
        var first = await Http.Post(client, "/api/auth/login", new { userName = "admin", password = "WrongPass2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, first.Status);
        var success = await Http.Post(client, "/api/auth/login", new { userName = "admin", password = ApiScenario.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, success.Status);
        var second = await Http.Post(client, "/api/auth/login", new { userName = "admin", password = "WrongPass2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, second.Status);
        var third = await Http.Post(client, "/api/auth/login", new { userName = "admin", password = "WrongPass2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, third.Status);
        var locked = await Http.Post(client, "/api/auth/login", new { userName = "admin", password = "WrongPass2026" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.Status);
    }

    [Fact]
    public async Task Unlisted_peer_cannot_split_the_login_bucket_with_forwarded_for()
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string?>
        {
            ["School:Login:FailureLimit"] = "2"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" },
                request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        }
        var spoofed = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" },
            request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.8"));
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.Status);
    }

    [Fact]
    public async Task Trusted_proxy_uses_one_forwarded_for_hop_as_the_login_client()
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string?>
        {
            ["School:Login:FailureLimit"] = "2",
            ["School:ReverseProxy:TrustedProxies:0"] = "127.0.0.1"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" },
                request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        }
        var sameClient = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" },
            request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.8, 203.0.113.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, sameClient.Status);
        var otherClient = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" },
            request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.8"));
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.Status);
    }

    [Fact]
    public async Task Forwarded_proto_from_an_unlisted_peer_does_not_satisfy_the_origin_check()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" },
            request =>
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://localhost");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            });
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Contains("跨站", response.Message);
    }

    [Fact]
    public async Task Trusted_proxy_forwarded_proto_matches_the_browser_origin()
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string?>
        {
            ["School:ReverseProxy:TrustedProxies:0"] = "127.0.0.1"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await Http.Post(client, "/api/auth/login", new { userName = "nobody", password = "WrongPass2026" },
            request =>
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://localhost");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            });
        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
    }

    [Fact]
    public async Task Setup_keeps_the_socket_peer_when_a_trusted_proxy_rewrites_the_client()
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string?>
        {
            ["School:ReverseProxy:TrustedProxies:0"] = "127.0.0.1"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await Http.Post(client, "/api/setup", new
        {
            schoolName = "本机初始化学校",
            userName = "admin",
            displayName = "管理员",
            password = ApiScenario.AdminPassword
        }, request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.50"));
        Assert.Equal(HttpStatusCode.OK, response.Status);
    }

    [Fact]
    public async Task Setup_rejects_a_remote_peer_that_forwards_a_loopback_address()
    {
        using var factory = new ApiFactory(IPAddress.Parse("203.0.113.9"), settings: new Dictionary<string, string?>
        {
            ["School:ReverseProxy:TrustedProxies:0"] = "203.0.113.9"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await Http.Post(client, "/api/setup", new
        {
            schoolName = "伪造本机学校",
            userName = "admin",
            displayName = "管理员",
            password = ApiScenario.AdminPassword
        }, request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1"));
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Contains("本机", response.Message);
    }
}
