using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using School.Training;

namespace School.Training.Tests;

[Collection("http")]
public sealed class HyperVSnapshotTests
{
    [Fact]
    public async Task List_returns_the_snapshot_without_waiting_for_every_node()
    {
        var commands = new HoldingHyperVCommand();
        using var factory = new ApiFactory(
            settings: Enumerable.Range(1, 5).ToDictionary(index => $"School:HyperV:Hosts:{index - 1}", index => (string?)$"hv{index}.example.com"),
            configureServices: services =>
            {
                services.RemoveAll<IHyperVCommand>();
                services.AddSingleton<IHyperVCommand>(commands);
            });
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setup = await Http.Post(admin, "/api/setup", new
        {
            schoolName = "快照学院",
            userName = "admin",
            displayName = "管理员",
            password = ApiScenario.AdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, setup.Status);
        var csrf = setup.Body.GetProperty("csrfToken").GetString()!;
        string firstNode = "";
        for (var index = 1; index <= 5; index++)
        {
            var created = await Http.Post(admin, "/api/nodes", new
            {
                name = "节点" + index,
                host = $"hv{index}.example.com",
                provider = "hyperv",
                port = 5986,
                totalCpu = 8,
                totalMemoryMb = 8192,
                totalDiskGb = 200,
                accountQuota = 10,
                virtualMachineQuota = 10
            }, csrf);
            Assert.Equal(HttpStatusCode.OK, created.Status);
            if (index == 1) firstNode = created.Body.GetProperty("id").GetString()!;
        }

        var watch = Stopwatch.StartNew();
        var first = await Http.Get(admin, "/api/hyperv/virtual-machines?pageSize=20");
        watch.Stop();
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"列表等待了 {watch.Elapsed}");
        Assert.True(first.Body.GetProperty("refreshing").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.Body.GetProperty("syncedAt").ValueKind);
        Assert.Equal(5, first.Body.GetProperty("pendingNodes").GetArrayLength());
        Assert.Equal(0, first.Body.GetProperty("total").GetInt32());

        var until = DateTime.UtcNow.AddSeconds(2);
        while (commands.Entered < 4 && DateTime.UtcNow < until) await Task.Delay(20);
        await Task.Delay(200);
        Assert.Equal(4, commands.Entered);

        var statusWatch = Stopwatch.StartNew();
        var status = await Http.Get(admin, "/api/nodes/" + firstNode + "/status");
        statusWatch.Stop();
        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.True(statusWatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(status.Body.GetProperty("refreshing").GetBoolean());
        Assert.Contains("正在同步", status.Body.GetProperty("message").GetString());

        commands.Hold.TrySetResult();
        JsonElement ready = default;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var response = await Http.Get(admin, "/api/hyperv/virtual-machines?pageSize=20");
            ready = response.Body;
            if (ready.GetProperty("total").GetInt32() == 5 && !ready.GetProperty("refreshing").GetBoolean()) break;
            await Task.Delay(50);
        }
        Assert.Equal(5, ready.GetProperty("total").GetInt32());
        Assert.False(ready.GetProperty("refreshing").GetBoolean());
        Assert.Equal(JsonValueKind.String, ready.GetProperty("syncedAt").ValueKind);
        Assert.Equal(0, ready.GetProperty("pendingNodes").GetArrayLength());
        Assert.Equal(0, ready.GetProperty("unavailableNodes").GetArrayLength());

        commands.Suffix = "-new";
        commands.DelayMs = 1500;
        factory.Services.GetRequiredService<HyperVProvider>().Freshness = TimeSpan.Zero;
        var staleWatch = Stopwatch.StartNew();
        var stale = await Http.Get(admin, "/api/hyperv/virtual-machines?pageSize=20");
        staleWatch.Stop();
        Assert.Equal(HttpStatusCode.OK, stale.Status);
        Assert.True(staleWatch.Elapsed < TimeSpan.FromSeconds(1), $"过期列表等待了 {staleWatch.Elapsed}");
        Assert.True(stale.Body.GetProperty("refreshing").GetBoolean());
        Assert.DoesNotContain("-new", stale.Body.GetRawText());
        Assert.Equal(5, stale.Body.GetProperty("total").GetInt32());
        factory.Services.GetRequiredService<HyperVProvider>().Freshness = TimeSpan.FromSeconds(10);

        var updated = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var response = await Http.Get(admin, "/api/hyperv/virtual-machines?pageSize=20");
            if (response.Body.GetRawText().Contains("-new", StringComparison.Ordinal)
                && !response.Body.GetProperty("refreshing").GetBoolean())
            {
                updated = true;
                break;
            }
            await Task.Delay(100);
        }
        Assert.True(updated);
    }

    sealed class HoldingHyperVCommand : IHyperVCommand
    {
        public int Entered;
        public int DelayMs;
        public string Suffix = "";
        public readonly TaskCompletionSource Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HyperVResult?> RunAsync(string host, string action, Guid? id, CancellationToken cancellationToken)
        {
            if (action == "inspect")
            {
                Interlocked.Increment(ref Entered);
                await Hold.Task.WaitAsync(cancellationToken);
                if (DelayMs > 0) await Task.Delay(DelayMs, cancellationToken);
            }
            var uuid = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(host)).AsSpan(0, 16));
            var running = action is "start" or "restart";
            return new HyperVResult(
                new HyperVMetrics(1, running ? 1 : 0, 5, 10, 15),
                [new HyperVMachine(uuid, host + Suffix, running ? "Running" : "Off", "192.0.2.40", 2, 2048)],
                action != "inspect",
                null);
        }
    }
}
