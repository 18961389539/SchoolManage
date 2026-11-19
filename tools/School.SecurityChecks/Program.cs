using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using School.Training;

var checks = new List<CheckResult>();
const bool requireSetupAccess = true;
var root = FindWorkspace();
var output = Path.Combine(root, "docs", "verification", "security-invariants.json");
var outputIndex = Array.IndexOf(args, "--output");
if (outputIndex >= 0)
{
    if (outputIndex + 1 >= args.Length) throw new ArgumentException("--output 需要指定 JSON 文件路径。");
    output = Path.GetFullPath(args[outputIndex + 1]);
}
string? error = null;
var setupStatus = "pending";
var setupCasesTested = 0;
try
{
    await Check("request cancellation is isolated to its own lease", () =>
    {
        using var lifetime = new FakeLifetime();
        var sessions = new SimulationSessions(lifetime);
        using var request = new CancellationTokenSource();
        Ensure(sessions.TryAcquire("device-a", "user-a", "A", "stamp", request.Token, out var a), "First request must acquire its device.");
        Ensure(sessions.TryAcquire("device-b", "user-b", "B", "stamp", CancellationToken.None, out var b), "Independent request must acquire another device.");
        request.Cancel();
        Ensure(a.Token.IsCancellationRequested, "Aborted request lease remained active.");
        Ensure(!b.Token.IsCancellationRequested, "Aborting one request canceled an unrelated lease.");
        sessions.Release(a); sessions.Release(b);
        return Task.CompletedTask;
    });

    await Check("application stopping cancels every active terminal without waiting for ApplicationStopped", async () =>
    {
        using var lifetime = new FakeLifetime();
        var sessions = new SimulationSessions(lifetime);
        var leases = new List<TerminalLease>();
        try
        {
            for (var index = 0; index < 32; index++)
            {
                Ensure(sessions.TryAcquire($"device-{index}", $"user-{index % 3}", "Operator", "stamp", CancellationToken.None, out var lease), "Active host rejected a free device.");
                leases.Add(lease);
            }
            var observers = leases.Select(async lease => { try { await Task.Delay(Timeout.Infinite, lease.Token); throw new InvalidOperationException("Uncanceled infinite delay completed."); } catch (OperationCanceledException) { } }).ToArray();
            lifetime.StopApplication();
            await Task.WhenAll(observers).WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(leases.All(x => x.Token.IsCancellationRequested), "Host stop left an active lease.");
            Ensure(!lifetime.ApplicationStopped.IsCancellationRequested, "Test must prove cancellation from ApplicationStopping alone.");
        }
        finally { foreach (var lease in leases) sessions.Release(lease); }
    });

    await Check("new requests cannot obtain a usable lease after stop or with an already-aborted request", () =>
    {
        using var lifetime = new FakeLifetime();
        var sessions = new SimulationSessions(lifetime);
        using var aborted = new CancellationTokenSource(); aborted.Cancel();
        Ensure(!sessions.TryAcquire("aborted", "user", "Operator", "stamp", aborted.Token, out var abortedLease), "An already-aborted request acquired a device.");
        Ensure(abortedLease.Token.IsCancellationRequested, "Rejected aborted lease exposed an active token.");
        lifetime.StopApplication();
        Ensure(!sessions.TryAcquire("stopping", "user", "Operator", "stamp", CancellationToken.None, out var stoppedLease), "Stopping host acquired a new device.");
        Ensure(stoppedLease.Token.IsCancellationRequested, "Rejected stopping lease exposed an active token.");
        Ensure(sessions.Count(["aborted", "stopping"]) == 0, "Rejected leases occupied devices.");
        return Task.CompletedTask;
    });

    await Check("stale release does not remove or cancel a later lease for the same device", () =>
    {
        using var lifetime = new FakeLifetime();
        var sessions = new SimulationSessions(lifetime);
        Ensure(sessions.TryAcquire("shared-device", "old-user", "Old", "old-stamp", CancellationToken.None, out var oldLease), "Could not acquire old lease.");
        sessions.Release(oldLease);
        Ensure(sessions.TryAcquire("shared-device", "new-user", "New", "new-stamp", CancellationToken.None, out var replacement), "Released device could not be acquired again.");
        Ensure(sessions.TryAcquire("other-device", "other-user", "Other", "stamp", CancellationToken.None, out var other), "Could not acquire independent lease.");
        sessions.Release(oldLease);
        Ensure(sessions.Owner("shared-device") == "New", "Stale release removed replacement owner.");
        Ensure(!replacement.Token.IsCancellationRequested && !other.Token.IsCancellationRequested, "Stale release canceled a newer lease.");
        Ensure(sessions.Count(["shared-device", "other-device"]) == 2, "Stale release corrupted lease occupancy.");
        lifetime.StopApplication();
        Ensure(replacement.Token.IsCancellationRequested && other.Token.IsCancellationRequested, "Disposing an old child lease damaged host cancellation propagation.");
        sessions.Release(replacement); sessions.Release(other);
        return Task.CompletedTask;
    });

    await Check("user revocation and cabinet reclaim preserve unrelated leases", () =>
    {
        using var lifetime = new FakeLifetime();
        var sessions = new SimulationSessions(lifetime);
        Ensure(sessions.TryAcquire("a-1", "user-a", "A", "stamp", CancellationToken.None, out var a1), "Could not acquire A1.");
        Ensure(sessions.TryAcquire("a-2", "user-a", "A", "stamp", CancellationToken.None, out var a2), "Could not acquire A2.");
        Ensure(sessions.TryAcquire("b-1", "user-b", "B", "stamp", CancellationToken.None, out var b), "Could not acquire B.");
        Ensure(sessions.RevokeUser("user-a") == 2, "User revocation count was incorrect.");
        Ensure(a1.Token.IsCancellationRequested && a2.Token.IsCancellationRequested, "User revocation missed a lease.");
        Ensure(!b.Token.IsCancellationRequested, "User revocation canceled another user's lease.");
        Ensure(sessions.Reclaim(["b-1"]) == 1 && b.Token.IsCancellationRequested, "Cabinet reclaim failed to cancel target lease.");
        foreach (var lease in new[] { a1, a2, b }) sessions.Release(lease);
        return Task.CompletedTask;
    });

    await Check("parallel acquire racing host shutdown never leaves a usable lease", async () =>
    {
        using var lifetime = new FakeLifetime();
        var sessions = new SimulationSessions(lifetime);
        using var start = new ManualResetEventSlim(false);
        var accepted = new ConcurrentBag<TerminalLease>();
        var work = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            start.Wait();
            if (sessions.TryAcquire($"parallel-{index}", "user", "Operator", "stamp", CancellationToken.None, out var lease)) accepted.Add(lease);
            else Ensure(lease.Token.IsCancellationRequested, "A unique-device acquisition was rejected without shutdown cancellation.");
        })).ToList();
        work.Add(Task.Run(() => { start.Wait(); lifetime.StopApplication(); }));
        start.Set();
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(accepted.All(x => x.Token.IsCancellationRequested), "Acquire/stop race left a usable token.");
        foreach (var lease in accepted) sessions.Release(lease);
        Ensure(sessions.Count(Enumerable.Range(0, 64).Select(x => $"parallel-{x}")) == 0, "Race cleanup left occupied devices.");
    });

    await Check("setup access distinguishes local endpoints from remote LAN clients and unspecified addresses", () =>
    {
        var cases = new (string? Remote, string? Local, bool Expected)[]
        {
            ("127.0.0.1", "127.0.0.1", true),
            ("::1", "::1", true),
            ("::ffff:127.0.0.1", "127.0.0.1", true),
            ("192.0.2.10", "192.0.2.10", true),
            ("::ffff:192.0.2.10", "192.0.2.10", true),
            ("192.0.2.10", "::ffff:192.0.2.10", true),
            ("192.0.2.99", "192.0.2.10", false),
            ("2001:db8::99", "2001:db8::10", false),
            (null, "127.0.0.1", false),
            ("127.0.0.1", null, false),
            (null, null, false),
            ("127.0.0.1", "0.0.0.0", false),
            ("::1", "::", false),
            ("0.0.0.0", "0.0.0.0", false),
            ("::", "::", false),
            ("127.0.0.1", "::ffff:0.0.0.0", false)
        };
        foreach (var item in cases)
        {
            var actual = SetupAccess.IsLocalEndpoint(item.Remote is null ? null : IPAddress.Parse(item.Remote), item.Local is null ? null : IPAddress.Parse(item.Local));
            Ensure(actual == item.Expected, $"SetupAccess mismatch for remote={item.Remote ?? "null"}, local={item.Local ?? "null"}.");
            setupCasesTested++;
        }
        return Task.CompletedTask;
    });
    await Check("unknown accounts and over-long passwords still perform one full hash verification", () =>
    {
        var hasher = new CountingHasher();
        var known = new AppUser { UserName = "operator", DisplayName = "Operator", Role = "Admin", PasswordHash = "stored-hash" };
        Ensure(!CredentialVerification.TryVerify(hasher, null, "wrong-password", out _), "An unknown account was accepted.");
        Ensure(hasher.Calls == 1, "An unknown account skipped password hashing, leaving an account-enumeration timing signal.");
        hasher.Reset();
        Ensure(!CredentialVerification.TryVerify(hasher, known, "wrong-password", out _), "A wrong password was accepted.");
        Ensure(hasher.Calls == 1, "A known account did not verify exactly once.");
        hasher.Reset();
        var overlong = new string('a', CredentialVerification.MaxPasswordLength + 40);
        Ensure(!CredentialVerification.TryVerify(hasher, known, overlong, out _), "An over-long password was accepted.");
        Ensure(hasher.Calls == 1 && hasher.LastPassword?.Length == CredentialVerification.MaxPasswordLength, "Over-long passwords must be truncated and hashed instead of rejected before hashing.");
        hasher.Reset();
        Ensure(!CredentialVerification.TryVerify(hasher, known, "", out _), "An empty password was accepted.");
        Ensure(hasher.Calls == 1, "An empty password skipped hashing.");

        var realHasher = new PasswordHasher<AppUser>();
        var real = new AppUser { UserName = "real-user", DisplayName = "Real", Role = "Admin" };
        real.PasswordHash = realHasher.HashPassword(real, "Correct-Password-123");
        Ensure(CredentialVerification.TryVerify(realHasher, real, "Correct-Password-123", out var authenticated) && authenticated.Id == real.Id, "A correct password was rejected.");
        Ensure(!CredentialVerification.TryVerify(realHasher, null, "Correct-Password-123", out _), "The placeholder hash accepted a real password for an unknown account.");
        return Task.CompletedTask;
    });
    setupStatus = "passed";
}
catch (Exception ex)
{
    error = ex.Message;
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
finally
{
    var assembly = typeof(SimulationSessions).Assembly.Location;
    var result = new
    {
        recordedAtUtc = DateTime.UtcNow,
        status = error is null ? "passed" : "failed",
        command = "dotnet run --project tools/School.SecurityChecks -c Release --" + (requireSetupAccess ? " --require-setup-access" : ""),
        mainAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
        checks,
        setupAccess = new { status = setupStatus, required = requireSetupAccess, testCaseCount = setupCasesTested },
        error,
        additionalNugetPackageReferences = 0,
        hostStarted = false,
        systemConfigurationChanged = false,
        scope = "In-process behavioral invariants with a fake IHostApplicationLifetime; no Windows service, HTTP listener or system configuration is started or changed. Does not replace SCM stop integration testing."
    };
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    await File.WriteAllTextAsync(output, json + Environment.NewLine);
    Console.WriteLine(json);
}

async Task Check(string name, Func<Task> action)
{
    var watch = Stopwatch.StartNew();
    try { await action(); checks.Add(new(name, "passed", Math.Round(watch.Elapsed.TotalMilliseconds, 2))); Console.WriteLine("PASS " + name); }
    catch { checks.Add(new(name, "failed", Math.Round(watch.Elapsed.TotalMilliseconds, 2))); throw; }
}
static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static string FindWorkspace()
{
    foreach (var initial in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        for (var current = new DirectoryInfo(initial); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "src", "School.Training", "School.Training.csproj"))) return current.FullName;
    }
    throw new DirectoryNotFoundException("请从 School 工作区运行本检查工具。");
}
internal sealed record CheckResult(string Name, string Status, double ElapsedMs);
internal sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();
    public CancellationToken ApplicationStarted => _started.Token;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => _stopped.Token;
    public void StopApplication() => _stopping.Cancel();
    public void Dispose() { _started.Dispose(); _stopping.Dispose(); _stopped.Dispose(); }
}
internal sealed class CountingHasher : IPasswordHasher<AppUser>
{
    public int Calls { get; private set; }
    public string? LastPassword { get; private set; }
    public string HashPassword(AppUser user, string password) => "stored-hash";
    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    { Calls++; LastPassword = providedPassword; return PasswordVerificationResult.Failed; }
    public void Reset() { Calls = 0; LastPassword = null; }
}
