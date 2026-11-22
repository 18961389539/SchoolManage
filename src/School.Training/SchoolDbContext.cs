using Microsoft.EntityFrameworkCore;

namespace School.Training;

public partial class SchoolDbContext(DbContextOptions<SchoolDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Profession> Professions => Set<Profession>();
    public DbSet<SchoolClass> Classes => Set<SchoolClass>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<AuditEntry> Audits => Set<AuditEntry>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>().HasIndex(x => x.UserName).IsUnique();
        modelBuilder.Entity<Profession>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Person>().HasIndex(x => new { x.Kind, x.Number }).IsUnique();
        modelBuilder.Entity<Person>().HasOne<AppUser>().WithOne().HasForeignKey<Person>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Person>().HasOne<SchoolClass>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SchoolClass>().HasIndex(x => new { x.ProfessionId, x.Name }).IsUnique();
        modelBuilder.Entity<SchoolClass>().HasOne<Profession>().WithMany().HasForeignKey(x => x.ProfessionId).OnDelete(DeleteBehavior.Restrict);
        // Audit lists page by (At DESC, Id DESC); the composite index also supports the
        // dashboard's "latest 8" query without a full scan plus sort.
        modelBuilder.Entity<AuditEntry>().HasIndex(x => new { x.At, x.Id }).HasDatabaseName("IX_Audits_At_Id");
        ConfigureInfrastructure(modelBuilder);
        ConfigureTeaching(modelBuilder);
        ConfigureExams(modelBuilder);
        ConfigureCamp(modelBuilder);
    }

    partial void ConfigureInfrastructure(ModelBuilder modelBuilder);
    partial void ConfigureTeaching(ModelBuilder modelBuilder);
    partial void ConfigureExams(ModelBuilder modelBuilder);
    partial void ConfigureCamp(ModelBuilder modelBuilder);
}

public class AppUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Student";
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Profession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Remark { get; set; } = "";
}

public class SchoolClass
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ProfessionId { get; set; } = "";
    public string Remark { get; set; } = "";
}

public class Person
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string Kind { get; set; } = "student";
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public string? ClassId { get; set; }
    public string Gender { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
}

public class AuditEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string UserName { get; set; } = "system";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
}

public class AppSetting
{
    public string Id { get; set; } = "";
    public string Value { get; set; } = "";
}

public class ImportBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string Kind { get; set; } = "student";
    public string RowsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Committed { get; set; }
    public string Status { get; set; } = ImportBatchStatus.Preview;
    public bool RemoveInvalidRows { get; set; }
    public int TotalCount { get; set; }
    public int ProcessedCount { get; set; }
    public int ImportedCount { get; set; }
    public int RemovedCount { get; set; }
    public string FailureMessage { get; set; } = "";
    public DateTime? QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
