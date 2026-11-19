using Microsoft.EntityFrameworkCore;

namespace School.Training;

public partial class SchoolDbContext
{
    public DbSet<DeviceType> DeviceTypes => Set<DeviceType>();
    public DbSet<Cabinet> Cabinets => Set<Cabinet>();
    public DbSet<TrainingDevice> Devices => Set<TrainingDevice>();
    public DbSet<TrainingNode> Nodes => Set<TrainingNode>();
    public DbSet<TrainingVirtualMachine> VirtualMachines => Set<TrainingVirtualMachine>();
    public DbSet<NodeAccountAssignment> NodeAccountAssignments => Set<NodeAccountAssignment>();

    partial void ConfigureInfrastructure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceType>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<Cabinet>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<TrainingNode>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<TrainingDevice>().HasOne<Cabinet>().WithMany().HasForeignKey(x => x.CabinetId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TrainingDevice>().HasOne<DeviceType>().WithMany().HasForeignKey(x => x.DeviceTypeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TrainingVirtualMachine>().HasOne<TrainingNode>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<NodeAccountAssignment>().HasIndex(x => new { x.NodeId, x.UserId }).IsUnique();
        modelBuilder.Entity<NodeAccountAssignment>().HasOne<TrainingNode>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<NodeAccountAssignment>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceType
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Newline { get; set; } = "CRLF";
    public string Backspace { get; set; } = "DEL";
    public string Remark { get; set; } = "";
}

public sealed class Cabinet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 23;
    public string JumpHost { get; set; } = "";
    public int JumpPort { get; set; } = 22;
    public string JumpUserName { get; set; } = "";
    public string JumpPasswordProtected { get; set; } = "";
    public string Remark { get; set; } = "";
    public bool IsSimulated { get; set; }
}

public sealed class TrainingDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string CabinetId { get; set; } = "";
    public string DeviceTypeId { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 23;
    public string ConnectionType { get; set; } = "simulated";
    public int SerialPortNumber { get; set; } = 1;
    public int WebPort { get; set; } = 80;
    public string WebProtocol { get; set; } = "http";
    public string UserName { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string Remark { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
    public string SavedConfigurationJson { get; set; } = "{}";
}

public sealed class TrainingNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Provider { get; set; } = "simulated";
    public int Port { get; set; } = 8006;
    public string UserName { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Remark { get; set; } = "";
    public int TotalCpu { get; set; } = 16;
    public int TotalMemoryMb { get; set; } = 32768;
    public int TotalDiskGb { get; set; } = 512;
    public string IpAddress { get; set; } = "192.0.2.10";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public string Gateway { get; set; } = "192.0.2.1";
    public long SimulatedClockOffsetSeconds { get; set; }
    public int AccountQuota { get; set; } = 200;
    public int VirtualMachineQuota { get; set; } = 50;
}

public sealed class NodeAccountAssignment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NodeId { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TrainingVirtualMachine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "stopped";
    public int CpuCount { get; set; } = 2;
    public int MemoryMb { get; set; } = 2048;
    public int DiskGb { get; set; } = 20;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public string Remark { get; set; } = "";
}

public sealed record DeviceTypeInput(string Name, string? Newline = null, string? Backspace = null, string? Remark = null);
public sealed record CabinetInput(string Name, string? Host = null, int Port = 23, string? JumpHost = null, int JumpPort = 22, string? JumpUserName = null, string? JumpPassword = null, string? Remark = null, bool IsSimulated = false);
public sealed record DeviceInput(string Name, string CabinetId, string DeviceTypeId, string? Host = null, int Port = 23, string ConnectionType = "simulated", int SerialPortNumber = 1, int WebPort = 80, string WebProtocol = "http", string? UserName = null, string? Password = null, string? Remark = null);
public sealed record NodeInput(string Name, string? Host = null, string Provider = "simulated", int Port = 8006, string? UserName = null, string? Password = null, bool Enabled = true, string? Remark = null, int TotalCpu = 16, int TotalMemoryMb = 32768, int TotalDiskGb = 512, int AccountQuota = 200, int VirtualMachineQuota = 50);
public sealed record VirtualMachineInput(string Name, string NodeId, int CpuCount = 2, int MemoryMb = 2048, int DiskGb = 20, string? Remark = null);
public sealed record InfrastructureIds(string[] Ids);
public sealed record NodeConfigurationInput(string IpAddress, string SubnetMask, string? Gateway = null, string? SystemTime = null);
public sealed record NodeAccountsInput(string[] UserIds);
