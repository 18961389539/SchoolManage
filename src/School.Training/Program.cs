using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using School.Training;

if (args.Length > 0 && args[0] is "--validate-database" or "--validate-backup")
{
    try
    {
        if (args[0] == "--validate-database")
        { if (args.Length != 2) throw new ArgumentException("用法：--validate-database <数据库文件>"); await DatabaseSafety.Validate(Path.GetFullPath(args[1])); }
        else
        { if (args.Length != 3) throw new ArgumentException("用法：--validate-backup <数据库文件> <密钥目录>"); await DatabaseSafety.ValidateBackup(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); }
        Console.WriteLine("Database and requested key validation passed.");
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "SchoolTraining");
var data = Environment.GetEnvironmentVariable("SCHOOL_DATA_DIR") ?? builder.Configuration["School:DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchoolTraining");
var dataDirectory = Path.GetFullPath(data);
builder.Configuration.AddJsonFile(Path.Combine(dataDirectory, "server.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
var paths = new AppPaths(dataDirectory, builder.Configuration["School:Backups:Directory"]);
if (DemoHost.IsEnabled(builder.Configuration)) DemoHost.Apply(builder);
else if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
var storage = StorageOptions.Load(builder.Configuration);
var fileLogger = new RollingFileLoggerProvider(paths.Logs, storage);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(storage);
builder.Services.AddSingleton(AutomaticBackupOptions.Load(builder.Configuration));
builder.Services.AddSingleton(AuditRetentionOptions.Load(builder.Configuration));
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddSingleton(fileLogger);
builder.Logging.AddProvider(fileLogger);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.RespectNullableAnnotations = true; o.SerializerOptions.RespectRequiredConstructorParameters = true; o.SerializerOptions.Converters.Add(new UtcDateTimeConverter()); });
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.Keys)).SetApplicationName("SchoolTraining");
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
var busyTimeoutSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("School:Database:BusyTimeoutSeconds") ?? 15, 1, 120);
builder.Services.AddDbContext<SchoolDbContext>(o => o.UseSqlite(new SqliteConnectionStringBuilder
{ DataSource = paths.Database, ForeignKeys = true, DefaultTimeout = busyTimeoutSeconds }.ToString()));
builder.Services.AddSingleton<IPasswordHasher<AppUser>>(new PasswordHasher<AppUser>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 600000 })));
var absoluteSessionHours = Math.Clamp(builder.Configuration.GetValue<int?>("School:Session:AbsoluteHours") ?? 24, 1, 168);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "SchoolTraining.Session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return ctx.Response.WriteAsJsonAsync(new { message = "请先登录" }); };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return ctx.Response.WriteAsJsonAsync(new { message = "没有执行此操作的权限" }); };
    o.Events.OnValidatePrincipal = async ctx =>
    {
        // Static assets and the SPA fallback never authorize through the principal, but the
        // browser still sends the session cookie with each one. Skipping the stamp lookup
        // avoids a database query per asset while every /api request is still revalidated.
        if (!ctx.HttpContext.Request.Path.StartsWithSegments("/api")) return;
        var db = ctx.HttpContext.RequestServices.GetRequiredService<SchoolDbContext>();
        var id = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier); var stamp = ctx.Principal?.FindFirstValue("stamp");
        if (id == null || !await db.Users.AnyAsync(x => x.Id == id && x.SecurityStamp == stamp, ctx.HttpContext.RequestAborted)
            || SessionPolicy.IsExpired(ctx.Principal?.FindFirstValue(SessionPolicy.StartedClaim), DateTimeOffset.UtcNow, absoluteSessionHours))
        { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(); }
    };
});
builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "SchoolTraining.Csrf"; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    o.AddPolicy("expensive", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = Limits.MaxUploadBytes);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = Limits.MaxUploadBytes);
builder.Services.AddInfrastructure();
builder.Services.AddSingleton<LicenseQuotaPolicy>();
builder.Services.AddSingleton<ImportJobQueue>();
builder.Services.AddSingleton<ImportPasswordReceipts>();
builder.Services.AddHostedService<ImportJobWorker>();
builder.Services.AddHostedService<ExamDeadlineWorker>();
builder.Services.AddHostedService<AutomaticBackupWorker>();
builder.Services.AddHostedService<AuditRetentionWorker>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await DatabaseMigrations.MigrateAsync(db, paths, storage, startupLogger);
    await DatabaseSafety.Validate(paths.Database, fullIntegrity: false);
    await DatabaseGuards.EnsureAsync(db);
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    if (DemoHost.IsEnabled(app.Configuration))
        await DemoSeed.EnsureAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>());
}
if (DemoHost.IsEnabled(app.Configuration))
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}
async Task HandleDatabaseFailure(HttpContext ctx, Exception exception)
{
    if (ctx.Response.HasStarted) return;
    var failure = DatabaseErrors.Classify(exception);
    if (failure == DatabaseFailure.Constraint)
    {
        app.Logger.LogWarning(exception, "Database constraint rejected request: {TraceIdentifier}", ctx.TraceIdentifier);
        ctx.Response.StatusCode = 409;
        var message = AdminInvariant.IsViolation(exception) ? "必须保留至少一个管理员" : "数据存在重复或关联冲突，请刷新后重试";
        await ctx.Response.WriteAsJsonAsync(new { message });
        return;
    }
    if (failure == DatabaseFailure.Busy)
    {
        app.Logger.LogWarning(exception, "Database busy: {TraceIdentifier}", ctx.TraceIdentifier);
        ctx.Response.StatusCode = 503;
        ctx.Response.Headers.RetryAfter = "5";
        await ctx.Response.WriteAsJsonAsync(new { message = "数据库正忙，请稍后重试", traceId = ctx.TraceIdentifier });
        return;
    }
    app.Logger.LogError(exception, "Database operation failed: {TraceIdentifier}", ctx.TraceIdentifier);
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new { message = "数据库操作失败，请查看服务日志", traceId = ctx.TraceIdentifier });
}
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff"; ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (DbUpdateConcurrencyException)
    {
        // Someone saved the same record between our version check and SaveChanges.
        app.Logger.LogInformation("Concurrency conflict: {TraceIdentifier}", ctx.TraceIdentifier);
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { message = Concurrency.Message, code = Concurrency.Code });
    }
    catch (DbUpdateException ex) { await HandleDatabaseFailure(ctx, ex); }
    catch (SqliteException ex) { await HandleDatabaseFailure(ctx, ex); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogInformation("Request cancelled by client: {TraceIdentifier}", ctx.TraceIdentifier);
        if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 499;
    }
    catch (BadHttpRequestException ex)
    {
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = ex.StatusCode; await ctx.Response.WriteAsJsonAsync(new { message = "请求内容或文件格式不正确" }); }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Request failed: {TraceIdentifier}", ctx.TraceIdentifier);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { message = "操作失败，请查看服务日志", traceId = ctx.TraceIdentifier }); }
    }
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseStatusCodePages(async status =>
{
    if (status.HttpContext.Request.Path.StartsWithSegments("/api"))
    {
        var code = status.HttpContext.Response.StatusCode;
        await status.HttpContext.Response.WriteAsJsonAsync(new { message = code switch { 400 => "请求字段不完整或格式不正确", 413 => $"请求文件超过{Limits.MaxUploadMegabytes}MB限制", 415 => "请求内容类型不受支持", 429 => "操作过于频繁，请稍后重试", _ => $"请求未完成（{code}）" } });
    }
});
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(originUri.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase)))
        { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { message = "拒绝跨站请求" }); return; }
        if (ctx.Request.Path != "/api/auth/login" && ctx.Request.Path != "/api/setup")
        {
            try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { message = "安全令牌已失效，请刷新页面后重试" }); return; }
        }
    }
    if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.FindFirstValue("mustChangePassword") == "true"
        && ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth") && ctx.Request.Path != "/api/status")
    { ctx.Response.StatusCode = 428; await ctx.Response.WriteAsJsonAsync(new { message = "首次登录或密码重置后，请先修改初始密码" }); return; }
    await next();
});
app.MapGet("/api/status", async (SchoolDbContext db, RollingFileLoggerProvider logs, CancellationToken ct) => Results.Ok(new
{
    initialized = await db.Users.AnyAsync(ct),
    product = "实训资源管理平台",
    version = BuildInfo.Version,
    schemaVersion = DatabaseSafety.SchemaVersion,
    logWriteFailures = logs.WriteFailures,
    maxUploadBytes = Limits.MaxUploadBytes,
    schoolName = await db.Settings.Where(x => x.Id == "schoolName").Select(x => x.Value).FirstOrDefaultAsync(ct),
    demo = DemoHost.IsEnabled(app.Configuration),
    demoPassword = DemoHost.IsEnabled(app.Configuration) ? DemoHost.Password : null,
    demoAccounts = DemoHost.IsEnabled(app.Configuration) ? DemoSeed.Accounts : null
}));
app.MapGet("/healthz", async (SchoolDbContext db, AppPaths paths, StorageOptions storage, CancellationToken ct) =>
{
    var database = false;
    try { database = await db.Database.CanConnectAsync(ct); } catch { }
    var dataFree = BackupService.AvailableFreeBytes(paths.Data);
    var backupFree = BackupService.AvailableFreeBytes(paths.Backups);
    var disk = dataFree is null || backupFree is null ? "unavailable"
        : dataFree < storage.BackupMinimumFreeBytes || backupFree < storage.BackupMinimumFreeBytes ? "low" : "ok";
    var healthy = database && disk == "ok";
    return Results.Json(new { status = healthy ? "healthy" : "unhealthy", checks = new { database = database ? "ok" : "failed", disk } },
        statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});
app.MapPost("/api/setup", async (SetupRequest body, HttpContext ctx, SchoolDbContext db, IPasswordHasher<AppUser> hasher, IAntiforgery antiforgery) =>
{
    if (!SetupAccess.IsLocalEndpoint(ctx.Connection.RemoteIpAddress, ctx.Connection.LocalIpAddress)) return Results.Json(new { message = "请在服务器本机浏览器完成首次初始化" }, statusCode: 403);
    if (await db.Users.AnyAsync(ctx.RequestAborted)) return Results.Conflict(new { message = "系统已初始化" });
    var error = Validation.UserName(body.UserName) ?? Validation.Password(body.Password);
    if (error != null || string.IsNullOrWhiteSpace(body.SchoolName) || string.IsNullOrWhiteSpace(body.DisplayName) || body.SchoolName.Length > 100 || body.DisplayName.Length > 100) return Results.BadRequest(new { message = error ?? "请填写学校名称与管理员姓名，每项最多100字" });
    var user = new AppUser { UserName = body.UserName.Trim().ToLowerInvariant(), DisplayName = body.DisplayName.Trim(), Role = "Admin" };
    user.PasswordHash = hasher.HashPassword(user, body.Password); db.Users.Add(user);
    db.Settings.Add(new AppSetting { Id = "schoolName", Value = body.SchoolName.Trim() }); Audit.Add(db, ctx, "初始化系统", user.UserName);
    await db.SaveChangesAsync(ctx.RequestAborted); await Session.SignIn(ctx, user); return Results.Ok(Session.Response(ctx, user, antiforgery));
}).RequireRateLimiting("login");
app.MapPost("/api/auth/login", async (LoginRequest body, HttpContext ctx, SchoolDbContext db, IPasswordHasher<AppUser> hasher, IAntiforgery antiforgery) =>
{
    var user = await db.Users.SingleOrDefaultAsync(x => x.UserName == body.UserName.Trim().ToLowerInvariant(), ctx.RequestAborted);
    if (!CredentialVerification.TryVerify(hasher, user, body.Password, out var authenticated, out var needsRehash))
    { Audit.Add(db, ctx, "登录失败", CredentialVerification.MaskAccount(body.UserName)); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Json(new { message = "账号或密码不正确" }, statusCode: 401); }
    // A hash written with an older iteration count is upgraded in place after a successful
    // login, so raising IterationCount never requires a mass password reset.
    if (needsRehash) authenticated.PasswordHash = hasher.HashPassword(authenticated, body.Password);
    await Session.SignIn(ctx, authenticated); db.Audits.Add(new AuditEntry { UserName = authenticated.UserName, Action = "登录", Target = authenticated.UserName }); await db.SaveChangesAsync(ctx.RequestAborted);
    return Results.Ok(Session.Response(ctx, authenticated, antiforgery));
}).RequireRateLimiting("login");
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/auth/me", async (HttpContext ctx, SchoolDbContext db, IAntiforgery antiforgery) =>
{
    var user = await db.Users.FindAsync([ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)], ctx.RequestAborted); return user == null ? Results.Unauthorized() : Results.Ok(Session.Response(ctx, user, antiforgery));
});
api.MapPost("/auth/logout", async (HttpContext ctx, SchoolDbContext db, SimulationSessions sessions) =>
{
    var user = await db.Users.FindAsync([ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)], ctx.RequestAborted);
    if (user != null) { user.SecurityStamp = Guid.NewGuid().ToString("N"); Audit.Add(db, ctx, "退出登录", user.UserName); await db.SaveChangesAsync(ctx.RequestAborted); sessions.RevokeUser(user.Id); }
    await ctx.SignOutAsync(); return Results.Ok(new { message = "已退出登录，终端会话已释放" });
});
api.MapPost("/auth/password", async (PasswordRequest body, HttpContext ctx, SchoolDbContext db, IPasswordHasher<AppUser> hasher, IAntiforgery antiforgery, SimulationSessions sessions) =>
{
    var user = await db.Users.SingleAsync(x => x.Id == ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), ctx.RequestAborted);
    if (body.CurrentPassword.Length > 128 || hasher.VerifyHashedPassword(user, user.PasswordHash, body.CurrentPassword) == PasswordVerificationResult.Failed) return Results.BadRequest(new { message = "当前密码不正确" });
    var error = Validation.Password(body.NewPassword); if (error != null) return Results.BadRequest(new { message = error });
    if (body.CurrentPassword == body.NewPassword) return Results.BadRequest(new { message = "新密码不能与当前密码相同" });
    user.PasswordHash = hasher.HashPassword(user, body.NewPassword); user.SecurityStamp = Guid.NewGuid().ToString("N"); user.MustChangePassword = false;
    Audit.Add(db, ctx, "修改密码", user.UserName); await db.SaveChangesAsync(ctx.RequestAborted); sessions.RevokeUser(user.Id); await Session.SignIn(ctx, user); return Results.Ok(Session.Response(ctx, user, antiforgery));
});
api.MapAcademicEndpoints(); api.MapImportEndpoints(); api.MapInfrastructureEndpoints(); api.MapOperationsEndpoints(); api.MapTeachingEndpoints(); api.MapExamQuestionEndpoints(); api.MapExamManagementEndpoints(); api.MapExamStudentEndpoints();
api.MapCourseFileEndpoints(); api.MapTeachingImportEndpoints(); api.MapTeachingPreviewEndpoints(); api.MapCampEndpoints();
api.MapHomeEndpoints(); api.MapClassroomStatisticsEndpoints(); api.MapSkillGraphEndpoints();
api.MapProfileEndpoints(); api.MapClassroomCollaborationEndpoints();
app.Map("/api/{**path}", () => Results.NotFound(new { message = "接口不存在" }));
app.UseDefaultFiles(); app.UseStaticFiles(); app.MapFallbackToFile("index.html"); app.Run();

namespace School.Training
{
    public record SetupRequest(string SchoolName, string UserName, string DisplayName, string Password);
    public record LoginRequest(string UserName, string Password);
    public record PasswordRequest(string CurrentPassword, string NewPassword);
    public static class Session
    {
        public static async Task SignIn(HttpContext ctx, AppUser user)
        {
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.UserName), new Claim(ClaimTypes.Role, user.Role), new Claim("stamp", user.SecurityStamp), new Claim("mustChangePassword", user.MustChangePassword ? "true" : "false"), new Claim(SessionPolicy.StartedClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()) };
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)); await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, ctx.User);
        }
        public static object Response(HttpContext ctx, AppUser user, IAntiforgery antiforgery) => new { user = new { user.Id, user.UserName, user.DisplayName, user.Role, user.MustChangePassword }, csrfToken = antiforgery.GetAndStoreTokens(ctx).RequestToken };
    }
}

// The integration tests host the real application through WebApplicationFactory.
public partial class Program { }
