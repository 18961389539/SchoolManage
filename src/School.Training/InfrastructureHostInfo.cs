using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace School.Training;

public static class InfrastructureHostInfo
{
    private static readonly object CacheLock = new();
    private static object? _cached;
    private static DateTime _cachedAt;

    public static object Collect()
    {
        lock (CacheLock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(10)) return _cached;
            var warnings = new List<string>();
            var processors = new List<ProcessorInfo>();
            var modules = new List<MemoryModuleInfo>();
            ulong? totalMemory = null, availableMemory = null;
            DateTime? bootTime = null;
            var isWindows = OperatingSystem.IsWindows();
            if (isWindows)
            {
                var hardware = CollectWindows(warnings);
                processors = hardware.Processors;
                modules = hardware.Modules;
                totalMemory = hardware.TotalMemory;
                availableMemory = hardware.AvailableMemory;
                bootTime = hardware.BootTime;
            }
            else warnings.Add("当前进程不在 Windows 上运行，未读取 WMI 硬件清单。");
            var networks = new List<NetworkInfo>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        var ip = adapter.GetIPProperties();
                        var addresses = ip.UnicastAddresses.Select(a => new AddressInfo(a.Address.ToString(), a.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6", a.Address.AddressFamily == AddressFamily.InterNetwork ? a.IPv4Mask?.ToString() : "", a.PrefixLength)).ToList();
                        var macAddress = adapter.GetPhysicalAddress().ToString();
                        var speed = adapter.Speed;
                        networks.Add(new(adapter.Name, adapter.Description, adapter.NetworkInterfaceType.ToString(), adapter.OperationalStatus.ToString(), macAddress.Length > 0 ? macAddress : null, speed >= 0 ? speed / 1_000_000 : null, addresses.Where(a => a.Family == "IPv4").Select(a => a.Address).ToArray(), addresses.Where(a => a.Family == "IPv6").Select(a => a.Address).ToArray(), addresses, ip.GatewayAddresses.Select(x => x.Address.ToString()).ToArray(), ip.DnsAddresses.Select(x => x.ToString()).ToArray()));
                    }
                    catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException) { warnings.Add($"网卡 {adapter.Name} 的部分信息不可用。"); }
                }
            }
            catch (NetworkInformationException) { warnings.Add("无法枚举网卡清单。"); }
            _cachedAt = DateTime.UtcNow;
            _cached = new
            {
                hostName = Environment.MachineName,
                osDescription = RuntimeInformation.OSDescription,
                operatingSystem = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                isSimulated = false,
                collectedAt = _cachedAt,
                cpu = new
                {
                    processorCount = processors.Count > 0 ? (int?)processors.Count : null,
                    physicalCoreCount = SumProcessorCounts(processors, processor => processor.CoreCount),
                    logicalCoreCount = isWindows ? SumProcessorCounts(processors, processor => processor.LogicalCoreCount) : Environment.ProcessorCount,
                    usagePercent = processors.Average(processor => processor.UsagePercent) is { } load ? (double?)Math.Round(load, 1) : null,
                    processors
                },
                memory = GetMemoryInfo(totalMemory, availableMemory, modules),
                networks,
                bootTime,
                uptimeSeconds = bootTime is { } boot ? (long?)Math.Max(0, (DateTime.UtcNow - boot).TotalSeconds) : null,
                processUptimeSeconds = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                warnings
            };
            return _cached;
        }
    }

    private static int? SumProcessorCounts(List<ProcessorInfo> processors, Func<ProcessorInfo, int?> count)
        => processors.Count > 0 && processors.All(processor => count(processor).HasValue) ? processors.Sum(count) : null;

    private static MemoryInfo GetMemoryInfo(ulong? total, ulong? available, List<MemoryModuleInfo> modules)
    {
        if (total is null && modules.Count > 0 && modules.All(module => module.CapacityBytes.HasValue))
            total = modules.Aggregate(0UL, (sum, module) => sum + module.CapacityBytes.GetValueOrDefault());
        double? usage = total is > 0 && available is { } free
            ? Math.Round(100.0 * (total.Value - Math.Min(free, total.Value)) / total.Value, 1)
            : null;
        return new(total, available, usage, modules);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WindowsHardware CollectWindows(List<string> warnings)
    {
        var data = new WindowsHardware();
        try
        {
            Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, LoadPercentage FROM Win32_Processor", item =>
                data.Processors.Add(new(Text(item, "Name"), PositiveInt(item, "NumberOfCores"), PositiveInt(item, "NumberOfLogicalProcessors"), Int(item, "LoadPercentage"))));
            if (data.Processors.Count == 0) warnings.Add("未获得 CPU 物理清单。");
        }
        catch (Exception ex) when (ExpectedWmiFailure(ex))
        {
            data.Processors.Clear();
            warnings.Add("无法读取 CPU 物理清单；请检查 Windows WMI 服务和服务账户权限。");
        }
        try
        {
            Query("SELECT Capacity, SMBIOSMemoryType, Speed, ConfiguredClockSpeed, Manufacturer, DeviceLocator FROM Win32_PhysicalMemory", item =>
                data.Modules.Add(new(ULong(item, "Capacity") is > 0 and var capacity ? capacity : null,
                    MemoryType(Int(item, "SMBIOSMemoryType")), PositiveInt(item, "ConfiguredClockSpeed") ?? PositiveInt(item, "Speed"),
                    Text(item, "Manufacturer"), Text(item, "DeviceLocator"))));
            if (data.Modules.Count == 0) warnings.Add("未获得内存条明细；虚拟机或部分硬件固件可能不提供该清单。");
        }
        catch (Exception ex) when (ExpectedWmiFailure(ex))
        {
            data.Modules.Clear();
            warnings.Add("无法读取内存条清单；请检查 WMI 服务和硬件支持情况。");
        }
        try
        {
            Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory, LastBootUpTime FROM Win32_OperatingSystem", item =>
            {
                var visibleMemoryKiB = ULong(item, "TotalVisibleMemorySize");
                data.TotalMemory = visibleMemoryKiB is > 0 ? visibleMemoryKiB * 1024 : null;
                data.AvailableMemory = ULong(item, "FreePhysicalMemory") * 1024;
                if (Text(item, "LastBootUpTime") is { } lastBoot) data.BootTime = ManagementDateTimeConverter.ToDateTime(lastBoot).ToUniversalTime();
            });
        }
        catch (Exception ex) when (ExpectedWmiFailure(ex)) { warnings.Add("无法读取操作系统物理内存或开机时间。"); }
        return data;
    }

    private sealed class WindowsHardware
    {
        public List<ProcessorInfo> Processors { get; } = [];
        public List<MemoryModuleInfo> Modules { get; } = [];
        public ulong? TotalMemory { get; set; }
        public ulong? AvailableMemory { get; set; }
        public DateTime? BootTime { get; set; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Query(string query, Action<ManagementBaseObject> action)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope("\\\\.\\root\\cimv2"), new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false, Rewindable = false });
        using var result = searcher.Get();
        foreach (ManagementBaseObject item in result) { using (item) action(item); }
    }
    private static bool ExpectedWmiFailure(Exception e) => e is ManagementException or UnauthorizedAccessException or COMException or PlatformNotSupportedException or ArgumentOutOfRangeException;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? Text(ManagementBaseObject item, string key)
        => Convert.ToString(item[key])?.Trim() is { Length: > 0 } text ? text : null;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int? Int(ManagementBaseObject item, string key) => item[key] is { } value ? Convert.ToInt32(value) : null;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int? PositiveInt(ManagementBaseObject item, string key) => Int(item, key) is > 0 and var value ? value : null;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ulong? ULong(ManagementBaseObject item, string key) => item[key] is { } value ? Convert.ToUInt64(value) : null;
    private static string? MemoryType(int? code) => code switch { null or 0 or 2 => null, 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5", _ => $"SMBIOS {code}" };
    private sealed record ProcessorInfo(string? Name, int? CoreCount, int? LogicalCoreCount, int? UsagePercent);
    private sealed record MemoryInfo(ulong? TotalBytes, ulong? AvailableBytes, double? UsagePercent, List<MemoryModuleInfo> Modules);
    private sealed record MemoryModuleInfo(ulong? CapacityBytes, string? Type, int? SpeedMhz, string? Manufacturer, string? Slot);
    private sealed record AddressInfo(string Address, string Family, string? SubnetMask, int PrefixLength);
    private sealed record NetworkInfo(string Name, string Description, string Type, string Status, string? MacAddress, long? SpeedMbps, string[] Ipv4, string[] Ipv6, List<AddressInfo> Addresses, string[] Gateways, string[] DnsServers);
}
