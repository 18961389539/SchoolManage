using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace School.Training.Tests;

[Collection("http")]
public sealed class DemoHostTests
{
    [Fact]
    public void Demo_mode_listens_on_all_interfaces_and_accepts_the_published_host()
    {
        Assert.Equal("http://0.0.0.0:5080", DemoHost.ListenUrl(null));
        Assert.Equal("http://0.0.0.0:5443", DemoHost.ListenUrl("5443"));
        Assert.Equal("http://0.0.0.0:5080", DemoHost.ListenUrl("0"));
        Assert.Equal("*", DemoHost.HostFilter(null));
        Assert.Equal("demo.example", DemoHost.HostFilter(" demo.example "));

        var enabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["School:Demo"] = "1" }).Build();
        var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["School:Demo"] = "0" }).Build();
        Assert.True(DemoHost.IsEnabled(enabled));
        Assert.False(DemoHost.IsEnabled(disabled));
    }

    [Fact]
    public void Missing_demo_password_is_generated_once_and_kept_out_of_the_web_root()
    {
        var data = Path.Combine(Path.GetTempPath(), "school-demo-password-" + Guid.NewGuid().ToString("N"), ".local", "demo-data");
        Directory.CreateDirectory(data);
        try
        {
            var file = DemoHost.PasswordFilePath(data);
            Assert.Equal(Path.GetFullPath(Path.Combine(data, "..", "demo-password.txt")), file);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}", file);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", file);

            var first = DemoHost.ResolvePassword(null, file);
            var second = DemoHost.ResolvePassword("short", file);
            Assert.Equal(first, second);
            Assert.Null(Validation.Password(first));
            Assert.Equal(first, File.ReadAllText(file).Trim());
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) AssertOwnerOnly(file);

            var supplied = DemoHost.GeneratePassword();
            Assert.Equal(supplied, DemoHost.ResolvePassword(supplied, file));
            Assert.Equal(supplied, File.ReadAllText(file).Trim());
        }
        finally
        {
            var root = Directory.GetParent(Directory.GetParent(data)!.FullName)!.FullName;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Remote_visitor_can_sign_in_to_a_seeded_demo()
    {
        var password = DemoHost.GeneratePassword();
        var previous = Environment.GetEnvironmentVariable(DemoHost.PasswordVariable);
        Environment.SetEnvironmentVariable(DemoHost.PasswordVariable, password);
        try
        {
            using var factory = new ApiFactory(IPAddress.Parse("203.0.113.20"), demo: true);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var configuration = factory.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("http://0.0.0.0:5080", configuration["urls"]);
            Assert.Equal("*", configuration["AllowedHosts"]);

            var statusJson = await client.GetStringAsync("/api/status");
            Assert.DoesNotContain(password, statusJson);
            Assert.DoesNotContain("demoPassword", statusJson, StringComparison.OrdinalIgnoreCase);
            var status = JsonSerializer.Deserialize<StatusBody>(statusJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(status);
            Assert.True(status.Initialized);
            Assert.True(status.Demo);
            Assert.Null(status.DemoPassword);
            Assert.Contains(status.DemoAccounts!, account => account.UserName == "demo" && account.Role == "Admin");
            Assert.Equal(DemoHost.SchoolName, status.SchoolName);
            var passwordFile = DemoHost.PasswordFilePath(Environment.GetEnvironmentVariable("SCHOOL_DATA_DIR")!);
            Assert.Equal(password, File.ReadAllText(passwordFile).Trim());
            Assert.DoesNotContain("wwwroot", passwordFile, StringComparison.OrdinalIgnoreCase);

            var setup = await client.PostAsJsonAsync("/api/setup", new { schoolName = "其他学校", userName = "other", displayName = "其他人", password = DemoHost.GeneratePassword() });
            Assert.Equal(HttpStatusCode.Forbidden, setup.StatusCode);

            var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "demo", password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var session = await login.Content.ReadFromJsonAsync<SessionBody>();
            Assert.False(session!.User.MustChangePassword);

            var classrooms = await client.GetFromJsonAsync<Paged>("/api/classrooms");
            Assert.True(classrooms!.Total >= 1);
            var positions = await client.GetFromJsonAsync<ItemList>("/api/camp/positions");
            Assert.NotEmpty(positions!.Items);

            var student = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var studentLogin = await student.PostAsJsonAsync("/api/auth/login", new { userName = "zhaozh", password });
            Assert.Equal(HttpStatusCode.OK, studentLogin.StatusCode);
            var studentSession = await studentLogin.Content.ReadFromJsonAsync<SessionBody>();
            Assert.False(studentSession!.User.MustChangePassword);
            var studentRooms = await student.GetFromJsonAsync<Paged>("/api/classrooms");
            Assert.True(studentRooms!.Total >= 1);

            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
            var before = await db.Users.CountAsync();
            await DemoSeed.EnsureAsync(db, hasher, password);
            Assert.Equal(before, await db.Users.CountAsync());
            Assert.Equal(1, await db.Nodes.CountAsync(node => node.Id == "simulation-node" && node.Provider == "simulated"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DemoHost.PasswordVariable, previous);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    static void AssertOwnerOnly(string file)
    {
        var mode = File.GetUnixFileMode(file);
        Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
    }

    sealed class StatusBody
    {
        public bool Initialized { get; set; }
        public bool Demo { get; set; }
        public string? DemoPassword { get; set; }
        public string? SchoolName { get; set; }
        public DemoAccountBody[]? DemoAccounts { get; set; }
    }

    sealed class DemoAccountBody
    {
        public string Role { get; set; } = "";
        public string UserName { get; set; } = "";
    }

    sealed class SessionBody
    {
        public SessionUserBody User { get; set; } = new();
    }

    sealed class SessionUserBody
    {
        public bool MustChangePassword { get; set; }
    }

    sealed class Paged
    {
        public int Total { get; set; }
    }

    sealed class ItemList
    {
        public JsonElement[] Items { get; set; } = [];
    }
}
