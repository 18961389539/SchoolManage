using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace School.Training;

public interface IHyperVCommand
{
    Task<HyperVResult?> RunAsync(string host, string action, Guid? id, CancellationToken cancellationToken);
}

public sealed class PowerShellHyperVCommand(ILogger<PowerShellHyperVCommand> logger) : IHyperVCommand
{
    public async Task<HyperVResult?> RunAsync(string host, string action, Guid? id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(action == "inspect" ? 12 : 40));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            }
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Command })
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { host, action, id }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result = await output;
            _ = await error;
            if (process.ExitCode == 0) return JsonSerializer.Deserialize<HyperVResult>(result, JsonSerializerOptions.Web);
            logger.LogWarning("Hyper-V {Action} on {Host} exited with code {Code}.", action, host, process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Hyper-V {Action} on {Host} timed out.", action, host);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            logger.LogWarning("Hyper-V {Action} on {Host} failed ({ErrorType}).", action, host, exception.GetType().Name);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        return null;
    }

    // Host and UUID arrive on standard input as data. The PowerShell command is constant.
    static readonly string Command = Convert.ToBase64String(Encoding.Unicode.GetBytes("""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $operation = {
            param($action, $id)
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $managedMarker = '(?m)^SchoolTraining\s*$'
            if ($action -ne 'inspect') {
                $vm = Get-VM -Id ([guid]$id) -ErrorAction SilentlyContinue
                if ($null -eq $vm -or $vm.Notes -cnotmatch $managedMarker) { return @{ error = 'notFound' } }
                switch ($action) {
                    'start' {
                        if ($vm.State -eq 'Off') { Start-VM -VM $vm }
                        elseif ($vm.State -ne 'Running') { return @{ error = 'state' } }
                    }
                    'stop' {
                        if ($vm.State -eq 'Running') { Stop-VM -VM $vm }
                        elseif ($vm.State -ne 'Off') { return @{ error = 'state' } }
                    }
                    'restart' {
                        if ($vm.State -ne 'Running') { return @{ error = 'state' } }
                        Restart-VM -VM $vm -Force
                    }
                    'destroy' {
                        if ($vm.State -ne 'Off') { return @{ error = 'state' } }
                        Remove-VM -VM $vm -Force
                    }
                    default { throw 'Unknown Hyper-V operation.' }
                }
                return @{ completed = $true }
            }

            $all = @(Get-VM)
            $machines = @(foreach ($vm in $all) {
                if ($vm.Notes -cnotmatch $managedMarker) { continue }
                $ip = Get-VMNetworkAdapter -VM $vm |
                    ForEach-Object { $_.IPAddresses } |
                    Where-Object {
                        $parsed = $null
                        [Net.IPAddress]::TryParse($_, [ref]$parsed) -and
                            $parsed.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
                            -not [Net.IPAddress]::IsLoopback($parsed) -and
                            -not $_.StartsWith('169.254.')
                    } | Select-Object -First 1
                @{
                    uuid = [string]$vm.Id
                    name = $vm.Name
                    status = [string]$vm.State
                    ipAddress = [string]$ip
                    cpuCount = $vm.ProcessorCount
                    memoryMb = [int][Math]::Ceiling($vm.MemoryStartup / 1MB)
                }
            })

            $hostOs = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
            $hostCpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction SilentlyContinue
            $vmRoot = (Get-VMHost).VirtualHardDiskPath
            $volume = if ($vmRoot) { [IO.Path]::GetPathRoot($vmRoot).TrimEnd('\') }
            $disk = Get-CimInstance Win32_LogicalDisk -ErrorAction SilentlyContinue |
                Where-Object { $_.DeviceID -eq $volume } | Select-Object -First 1
            $metrics = @{
                totalVms = $all.Count
                runningVms = @($all | Where-Object State -eq 'Running').Count
                cpuUsagePercent = if ($hostCpu) { $hostCpu.PercentProcessorTime } else { $null }
                memoryUsagePercent = if ($hostOs.TotalVisibleMemorySize) {
                    [Math]::Round(100.0 * (1 - $hostOs.FreePhysicalMemory / $hostOs.TotalVisibleMemorySize), 1)
                } else { $null }
                diskUsagePercent = if ($disk.Size) {
                    [Math]::Round(100.0 * (1 - $disk.FreeSpace / $disk.Size), 1)
                } else { $null }
            }
            @{ metrics = $metrics; machines = $machines }
        }
        $result = if ($request.host -in @('localhost', $env:COMPUTERNAME)) {
            & $operation $request.action $request.id
        } else {
            $options = New-PSSessionOption -OpenTimeout 8000 -OperationTimeout 30000
            Invoke-Command -ComputerName $request.host -UseSSL -Authentication Kerberos -SessionOption $options -ScriptBlock $operation -ArgumentList $request.action, $request.id
        }
        ConvertTo-Json -InputObject $result -Depth 5 -Compress
        """));
}

public readonly record struct HyperVView(HyperVResult? Result, DateTime? SyncedAt, bool Refreshing, bool Configured);

public sealed class HyperVProvider(IConfiguration configuration, IHyperVCommand commands, IHostApplicationLifetime lifetime)
{
    public const string Name = "hyperv";
    public const int MaxConcurrentInspections = 4;
    internal TimeSpan Freshness { get; set; } = TimeSpan.FromSeconds(10);
    readonly SemaphoreSlim inspections = new(MaxConcurrentInspections, MaxConcurrentInspections);
    readonly ConcurrentDictionary<string, Snapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

    sealed class Snapshot
    {
        public readonly object Sync = new();
        public HyperVResult? Result;
        public DateTime? SyncedAt;
        public int Refreshing;
        public bool Again;
    }

    public bool IsConfigured(string host)
        => OperatingSystem.IsWindows() && configuration.GetSection("School:HyperV:Hosts").GetChildren()
            .Any(entry => string.Equals(entry.Value?.Trim(), host, StringComparison.OrdinalIgnoreCase));

    public HyperVView Read(string host)
    {
        if (!IsConfigured(host)) return new(null, null, false, false);
        var snapshot = snapshots.GetOrAdd(host, _ => new Snapshot());
        HyperVResult? result;
        DateTime? syncedAt;
        var stale = false;
        lock (snapshot.Sync)
        {
            result = snapshot.Result;
            syncedAt = snapshot.SyncedAt;
            stale = syncedAt == null || DateTime.UtcNow - syncedAt.Value >= Freshness;
        }
        var refreshing = stale ? BeginRefresh(host, snapshot) : Busy(snapshot);
        return new(syncedAt == null ? null : result, syncedAt, refreshing, true);
    }

    public async Task<HyperVView> WaitForSyncAsync(string host, CancellationToken cancellationToken)
    {
        var view = Read(host);
        if (!view.Configured || view.SyncedAt != null || !snapshots.TryGetValue(host, out var snapshot)) return view;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            while (Busy(snapshot))
                await Task.Delay(50, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return Read(host);
    }

    public async Task<HyperVResult?> ControlAsync(string host, Guid id, string action, CancellationToken cancellationToken)
    {
        if (!IsConfigured(host)) return null;
        await inspections.WaitAsync(cancellationToken);
        HyperVResult? result;
        try { result = await commands.RunAsync(host, action, id, cancellationToken); }
        finally { inspections.Release(); }
        if (result?.Completed == true) Remember(host, id, action);
        if (result != null) BeginRefresh(host, snapshots.GetOrAdd(host, _ => new Snapshot()));
        return result;
    }

    static bool Busy(Snapshot snapshot)
    {
        lock (snapshot.Sync) return snapshot.Refreshing == 1;
    }

    bool BeginRefresh(string host, Snapshot snapshot)
    {
        lock (snapshot.Sync)
        {
            snapshot.Again = true;
            if (snapshot.Refreshing == 1) return true;
            snapshot.Refreshing = 1;
            snapshot.Again = false;
        }
        _ = RefreshAsync(host, snapshot);
        return true;
    }

    async Task RefreshAsync(string host, Snapshot snapshot)
    {
        try
        {
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                var acquired = false;
                try
                {
                    await inspections.WaitAsync(lifetime.ApplicationStopping);
                    acquired = true;
                    var result = await commands.RunAsync(host, "inspect", null, lifetime.ApplicationStopping);
                    lock (snapshot.Sync)
                    {
                        snapshot.Result = result;
                        snapshot.SyncedAt = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    return;
                }
                finally
                {
                    if (acquired) inspections.Release();
                }
                lock (snapshot.Sync)
                {
                    if (!snapshot.Again) return;
                    snapshot.Again = false;
                }
            }
        }
        finally
        {
            bool restart;
            lock (snapshot.Sync)
            {
                restart = snapshot.Again && !lifetime.ApplicationStopping.IsCancellationRequested;
                if (restart) snapshot.Again = false;
                else snapshot.Refreshing = 0;
            }
            if (restart) _ = RefreshAsync(host, snapshot);
        }
    }

    void Remember(string host, Guid id, string action)
    {
        var snapshot = snapshots.GetOrAdd(host, _ => new Snapshot());
        lock (snapshot.Sync)
        {
            if (snapshot.Result?.Machines == null) return;
            var machines = snapshot.Result.Machines;
            if (action == "destroy")
                snapshot.Result = snapshot.Result with { Machines = machines.Where(machine => machine.Uuid != id).ToArray() };
            else if (action is "start" or "restart" or "stop")
            {
                var status = action == "stop" ? "Off" : "Running";
                snapshot.Result = snapshot.Result with
                {
                    Machines = machines.Select(machine => machine.Uuid == id ? machine with { Status = status } : machine).ToArray()
                };
            }
        }
    }
}

public sealed class HyperVSnapshotWorker(HyperVProvider provider, IServiceScopeFactory scopes, ILogger<HyperVSnapshotWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
                var hosts = await db.Nodes.AsNoTracking().Where(node => node.Provider == HyperVProvider.Name)
                    .Select(node => node.Host).Distinct().ToListAsync(stoppingToken);
                foreach (var host in hosts) provider.Read(host);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Hyper-V snapshot refresh failed ({ErrorType}).", exception.GetType().Name);
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public sealed record HyperVResult(HyperVMetrics? Metrics, HyperVMachine[]? Machines, bool Completed, string? Error);
public sealed record HyperVMetrics(int TotalVms, int RunningVms, double? CpuUsagePercent, double? MemoryUsagePercent, double? DiskUsagePercent);
public sealed record HyperVMachine(Guid Uuid, string Name, string Status, string IpAddress, int CpuCount, int MemoryMb);
