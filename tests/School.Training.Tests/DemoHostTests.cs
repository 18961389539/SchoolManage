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
    public async Task Remote_visitor_can_sign_in_to_a_seeded_demo()
    {
        using var factory = new ApiFactory(IPAddress.Parse("203.0.113.20"), demo: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("http://0.0.0.0:5080", configuration["urls"]);
        Assert.Equal("*", configuration["AllowedHosts"]);

        var statusJson = await client.GetStringAsync("/api/status");
        Assert.DoesNotContain("demoPassword", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.DemoPassword!, statusJson, StringComparison.Ordinal);
        var status = JsonSerializer.Deserialize<StatusBody>(statusJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(status);
        Assert.True(status.Initialized);
        Assert.True(status.Demo);
        Assert.Contains(status.DemoAccounts!, account => account.UserName == "demo" && account.Role == "Admin");
        Assert.Equal(DemoHost.SchoolName, status.SchoolName);

        var setup = await client.PostAsJsonAsync("/api/setup", new { schoolName = "其他学校", userName = "other", displayName = "其他人", password = "OtherPass12345" });
        Assert.Equal(HttpStatusCode.Forbidden, setup.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "demo", password = factory.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<SessionBody>();
        Assert.False(session!.User.MustChangePassword);

        var classrooms = await client.GetFromJsonAsync<Paged>("/api/classrooms");
        Assert.True(classrooms!.Total >= 1);
        var positions = await client.GetFromJsonAsync<ItemList>("/api/camp/positions");
        Assert.NotEmpty(positions!.Items);

        var student = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var studentLogin = await student.PostAsJsonAsync("/api/auth/login", new { userName = "zhaozh", password = factory.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, studentLogin.StatusCode);
        var studentSession = await studentLogin.Content.ReadFromJsonAsync<SessionBody>();
        Assert.False(studentSession!.User.MustChangePassword);
        var studentRooms = await student.GetFromJsonAsync<Paged>("/api/classrooms");
        Assert.True(studentRooms!.Total >= 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
        var before = await db.Users.CountAsync();
        await DemoSeed.EnsureAsync(db, hasher, factory.DemoPassword!);
        Assert.Equal(before, await db.Users.CountAsync());
        Assert.Equal(1, await db.Nodes.CountAsync(node => node.Id == "simulation-node" && node.Provider == "simulated"));
    }

    sealed class StatusBody
    {
        public bool Initialized { get; set; }
        public bool Demo { get; set; }
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
