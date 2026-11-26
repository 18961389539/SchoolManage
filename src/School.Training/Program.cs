using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using School.Training;

if (args.Length > 0 && args[0] is "--validate-database" or "--validate-backup")
{
    try
    {
        if (args[0] == "--validate-database")
        { if (args.Length != 2) throw new ArgumentException("用法：--validate-database <数据库文件>"); await DatabaseSafety.Validate(Path.GetFullPath(args[1])); }
        else
        {
            if (args.Length != 3) throw new ArgumentException("用法：--validate-backup <数据库文件> <密钥目录>");
            var keysDirectory = Path.GetFullPath(args[2]);
            await DatabaseSafety.ValidateBackup(Path.GetFullPath(args[1]), keysDirectory, KeyProtectionOptions.Load(ValidationConfiguration(keysDirectory)));
        }
        Console.WriteLine("Database and requested key validation passed.");
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    return;
}
// Exporting the contract starts the host only long enough to read route metadata.
// Keep that process off the school data directory and off the background workers.
string? exportOpenApiPath = null;
var exportFlag = Array.FindIndex(args, static arg => arg == "--export-openapi");
if (exportFlag >= 0)
{
    if (args.Length != exportFlag + 2) throw new ArgumentException("用法：--export-openapi <输出文件>");
    exportOpenApiPath = Path.GetFullPath(args[exportFlag + 1]);
    args = [.. args.Where((_, index) => index != exportFlag && index != exportFlag + 1)];
    var scratch = Path.Combine(Path.GetTempPath(), "school-openapi-contract");
    Directory.CreateDirectory(scratch);
    Environment.SetEnvironmentVariable("SCHOOL_DATA_DIR", scratch);
}
var exportingOpenApi = exportOpenApiPath != null;
static IConfiguration ValidationConfiguration(string keysDirectory)
{
    var configuration = new ConfigurationBuilder();
    var data = Environment.GetEnvironmentVariable("SCHOOL_DATA_DIR");
    if (string.IsNullOrWhiteSpace(data)) data = Path.GetDirectoryName(keysDirectory);
    if (!string.IsNullOrWhiteSpace(data))
        configuration.AddJsonFile(Path.Combine(data, "server.json"), optional: true, reloadOnChange: false);
    configuration.AddEnvironmentVariables();
    return configuration.Build();
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
if (exportingOpenApi)
{
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
}
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
builder.Services.AddOpenApi("contract", options =>
{
    options.ShouldInclude = description => description.GroupName == "contract";
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "实训资源平台核心契约";
        document.Info.Version = BuildInfo.Version;
        document.Info.Description = "会话、运行状态和题库题目读取的具名响应。其余接口仍由 web/src/types.ts 手工维护，迁入具名响应后再加入 contract 文档。";
        return Task.CompletedTask;
    });
});
var keyProtection = KeyProtectionOptions.Load(builder.Configuration);
var keyMaterial = KeyProtectionMaterial.Prepare(paths.Keys, keyProtection, fileLogger.CreateLogger("KeyProtection"));
builder.Services.AddSingleton(keyProtection);
builder.Services.AddSingleton(keyMaterial);
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.Keys)).SetApplicationName("SchoolTraining");
KeyProtectionMaterial.Apply(dataProtection, keyMaterial.Certificate);
var busyTimeoutSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("School:Database:BusyTimeoutSeconds") ?? 15, 1, 120);
builder.Services.AddSingleton<HomeSnapshotCache>();
builder.Services.AddSingleton<SecurityStampCache>();
builder.Services.AddDbContext<SchoolDbContext>(o => o.UseSqlite(new SqliteConnectionStringBuilder
{ DataSource = paths.Database, ForeignKeys = true, DefaultTimeout = busyTimeoutSeconds }.ToString()));
builder.Services.AddSingleton<IPasswordHasher<AppUser>>(new PasswordHasher<AppUser>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = PasswordHashing.IterationCount })));
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
        var id = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ctx.Principal?.FindFirstValue("stamp");
        var expired = SessionPolicy.IsExpired(ctx.Principal?.FindFirstValue(SessionPolicy.StartedClaim), DateTimeOffset.UtcNow, absoluteSessionHours);
        if (id == null || stamp == null || expired)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync();
            return;
        }
        var stamps = ctx.HttpContext.RequestServices.GetRequiredService<SecurityStampCache>();
        if (!stamps.TryResolve(id, stamp, out var accepted))
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<SchoolDbContext>();
            var current = await db.Users.AsNoTracking().Where(user => user.Id == id)
                .Select(user => user.SecurityStamp).FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);
            if (current == null)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync();
                return;
            }
            stamps.Remember(id, current);
            accepted = current == stamp;
        }
        if (!accepted)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync();
        }
    };
});
builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "SchoolTraining.Csrf"; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; });
var loginThrottle = LoginThrottleOptions.Load(builder.Configuration);
var reverseProxy = ReverseProxyOptions.Load(builder.Configuration);
builder.Services.AddSingleton(loginThrottle);
builder.Services.AddSingleton(reverseProxy);
builder.Services.AddSingleton<LoginFailureLimiter>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // Per client IP after trusted forwarded headers, so a classroom NAT is a burst, not a lockout.
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(LoginFailureLimiter.ClientKey(ctx.Connection.RemoteIpAddress),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginThrottle.IpPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
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
if (!exportingOpenApi)
{
    builder.Services.AddHostedService<ImportJobWorker>();
    builder.Services.AddHostedService<ExamDeadlineWorker>();
    builder.Services.AddHostedService<AutomaticBackupWorker>();
    builder.Services.AddHostedService<AuditRetentionWorker>();
    builder.Services.AddHostedService<HyperVSnapshotWorker>();
}
var app = builder.Build();
if (!exportingOpenApi)
{
    using var scope = app.Services.CreateScope();
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
    // The public demo sits behind an unlisted hosting proxy. School servers must not set SCHOOL_DEMO;
    // they list School:ReverseProxy:TrustedProxies and never trust an arbitrary forwarder.
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}
else if (reverseProxy.IsConfigured)
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Connection.RemoteIpAddress is { } peer)
            ctx.Items[ReverseProxyOptions.ConnectionPeerItem] = peer;
        await next();
    });
    var forwarded = new ForwardedHeadersOptions();
    reverseProxy.Apply(forwarded);
    app.Logger.LogInformation("Honoring one forwarded hop from {ProxyCount} trusted proxies and {NetworkCount} networks.",
        reverseProxy.TrustedProxies.Count, reverseProxy.TrustedNetworks.Count);
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
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
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
app.MapGet("/api/status", async (SchoolDbContext db, RollingFileLoggerProvider logs, CancellationToken ct) =>
{
    var demo = DemoHost.IsEnabled(app.Configuration);
    return new StatusResponse(
        await db.Users.AnyAsync(ct),
        "实训资源管理平台",
        BuildInfo.Version,
        DatabaseSafety.SchemaVersion,
        logs.WriteFailures,
        Limits.MaxUploadBytes,
        await db.Settings.Where(x => x.Id == "schoolName").Select(x => x.Value).FirstOrDefaultAsync(ct),
        demo,
        demo ? DemoHost.Password : null,
        demo ? DemoSeed.Accounts.Select(account => new StatusDemoAccount(account.Role, account.UserName, account.DisplayName)).ToArray() : null);
}).WithGroupName("contract");
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
    if (!SetupAccess.IsLocalEndpoint(ReverseProxyOptions.ConnectionPeer(ctx), ctx.Connection.LocalIpAddress)) return Results.Json(new { message = "请在服务器本机浏览器完成首次初始化" }, statusCode: 403);
    if (await db.Users.AnyAsync(ctx.RequestAborted)) return Results.Conflict(new { message = "系统已初始化" });
    var error = Validation.UserName(body.UserName) ?? Validation.Password(body.Password);
    if (error != null || string.IsNullOrWhiteSpace(body.SchoolName) || string.IsNullOrWhiteSpace(body.DisplayName) || body.SchoolName.Length > 100 || body.DisplayName.Length > 100) return Results.BadRequest(new { message = error ?? "请填写学校名称与管理员姓名，每项最多100字" });
    var user = new AppUser { UserName = body.UserName.Trim().ToLowerInvariant(), DisplayName = body.DisplayName.Trim(), Role = "Admin" };
    user.PasswordHash = hasher.HashPassword(user, body.Password); db.Users.Add(user);
    db.Settings.Add(new AppSetting { Id = "schoolName", Value = body.SchoolName.Trim() }); Audit.Add(db, ctx, "初始化系统", user.UserName);
    await db.SaveChangesAsync(ctx.RequestAborted); await Session.SignIn(ctx, user); return Results.Ok(Session.Response(ctx, user, antiforgery));
}).RequireRateLimiting("login").WithGroupName("contract").Produces<SessionResponse>();
app.MapPost("/api/auth/login", async (LoginRequest body, HttpContext ctx, SchoolDbContext db, IPasswordHasher<AppUser> hasher, IAntiforgery antiforgery, LoginFailureLimiter failures) =>
{
    var account = body.UserName.Trim().ToLowerInvariant();
    var clientIp = LoginFailureLimiter.ClientKey(ctx.Connection.RemoteIpAddress);
    var bucket = LoginFailureLimiter.AccountKey(account);
    if (failures.IsLocked(clientIp, bucket, DateTimeOffset.UtcNow, out var retryAfter))
    {
        ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(new { message = "该账号登录失败次数过多，请稍后再试" }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    var user = await db.Users.SingleOrDefaultAsync(x => x.UserName == account, ctx.RequestAborted);
    if (!CredentialVerification.TryVerify(hasher, user, body.Password, out var authenticated, out var needsRehash))
    {
        failures.RecordFailure(clientIp, bucket, DateTimeOffset.UtcNow);
        Audit.Add(db, ctx, "登录失败", CredentialVerification.MaskAccount(body.UserName));
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.Json(new { message = "账号或密码不正确" }, statusCode: 401);
    }
    // A hash written with an older iteration count is upgraded in place after a successful
    // login, so raising IterationCount never requires a mass password reset.
    if (needsRehash) authenticated.PasswordHash = hasher.HashPassword(authenticated, body.Password);
    failures.RecordSuccess(clientIp, bucket);
    await Session.SignIn(ctx, authenticated); db.Audits.Add(new AuditEntry { UserName = authenticated.UserName, Action = "登录", Target = authenticated.UserName }); await db.SaveChangesAsync(ctx.RequestAborted);
    return Results.Ok(Session.Response(ctx, authenticated, antiforgery));
}).RequireRateLimiting("login").WithGroupName("contract").Produces<SessionResponse>();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/auth/me", async (HttpContext ctx, SchoolDbContext db, IAntiforgery antiforgery) =>
{
    var user = await db.Users.FindAsync([ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)], ctx.RequestAborted); return user == null ? Results.Unauthorized() : Results.Ok(Session.Response(ctx, user, antiforgery));
}).WithGroupName("contract").Produces<SessionResponse>();
api.MapPost("/auth/logout", async (HttpContext ctx, SchoolDbContext db, SimulationSessions sessions, SecurityStampCache stamps) =>
{
    var user = await db.Users.FindAsync([ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)], ctx.RequestAborted);
    if (user != null)
    {
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        Audit.Add(db, ctx, "退出登录", user.UserName);
        await db.SaveChangesAsync(ctx.RequestAborted);
        stamps.Revoke(user.Id, user.SecurityStamp);
        sessions.RevokeUser(user.Id);
    }
    await ctx.SignOutAsync();
    return Results.Ok(new { message = "已退出登录，终端会话已释放" });
});
api.MapPost("/auth/password", async (PasswordRequest body, HttpContext ctx, SchoolDbContext db, IPasswordHasher<AppUser> hasher, IAntiforgery antiforgery, SimulationSessions sessions, SecurityStampCache stamps) =>
{
    var user = await db.Users.SingleAsync(x => x.Id == ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), ctx.RequestAborted);
    if (body.CurrentPassword.Length > 128 || hasher.VerifyHashedPassword(user, user.PasswordHash, body.CurrentPassword) == PasswordVerificationResult.Failed) return Results.BadRequest(new { message = "当前密码不正确" });
    var error = Validation.Password(body.NewPassword); if (error != null) return Results.BadRequest(new { message = error });
    if (body.CurrentPassword == body.NewPassword) return Results.BadRequest(new { message = "新密码不能与当前密码相同" });
    user.PasswordHash = hasher.HashPassword(user, body.NewPassword); user.SecurityStamp = Guid.NewGuid().ToString("N"); user.MustChangePassword = false;
    Audit.Add(db, ctx, "修改密码", user.UserName);
    await db.SaveChangesAsync(ctx.RequestAborted);
    stamps.Revoke(user.Id, user.SecurityStamp);
    sessions.RevokeUser(user.Id);
    await Session.SignIn(ctx, user);
    return Results.Ok(Session.Response(ctx, user, antiforgery));
}).WithGroupName("contract").Produces<SessionResponse>();
api.MapAcademicEndpoints(); api.MapImportEndpoints(); api.MapInfrastructureEndpoints(); api.MapOperationsEndpoints(); api.MapTeachingEndpoints(); api.MapExamQuestionEndpoints(); api.MapExamManagementEndpoints(); api.MapExamStudentEndpoints();
api.MapCourseFileEndpoints(); api.MapTeachingImportEndpoints(); api.MapTeachingPreviewEndpoints(); api.MapCampEndpoints();
api.MapHomeEndpoints(); api.MapClassroomStatisticsEndpoints(); api.MapSkillGraphEndpoints();
api.MapProfileEndpoints(); api.MapClassroomCollaborationEndpoints();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
if (exportOpenApiPath != null)
{
    await app.StartAsync();
    try
    {
        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("contract");
        var document = await provider.GetOpenApiDocumentAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(exportOpenApiPath)!);
        await using var output = new FileStream(exportOpenApiPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await document.SerializeAsJsonAsync(output, OpenApiSpecVersion.OpenApi3_1);
    }
    finally { await app.StopAsync(); }
    return;
}
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
        public static SessionResponse Response(HttpContext ctx, AppUser user, IAntiforgery antiforgery) => new(
            new SessionUserResponse(user.Id, user.UserName, user.DisplayName, user.Role, user.MustChangePassword),
            antiforgery.GetAndStoreTokens(ctx).RequestToken ?? "");
    }
}

// The integration tests host the real application through WebApplicationFactory.
public partial class Program { }
