namespace School.Training;

/// <summary>
/// Operational limits for files written by the service. Values are configurable
/// under <c>School:Logs</c>, <c>School:Backups</c> and <c>School:Migrations</c>.
/// </summary>
public sealed class StorageOptions
{
    public int LogRetentionDays { get; init; } = 14;
    public long LogMaxFileBytes { get; init; } = 10 * 1024 * 1024;
    public long LogMaxTotalBytes { get; init; } = 200 * 1024 * 1024;
    public int BackupRetentionCount { get; init; } = 20;
    public long BackupMaxTotalBytes { get; init; } = 5L * 1024 * 1024 * 1024;
    public long BackupMinimumFreeBytes { get; init; } = 256L * 1024 * 1024;
    public int PreMigrationBackupCount { get; init; } = 5;

    public static StorageOptions Load(IConfiguration configuration)
    {
        long Read(string key, long fallback, long minimum, long maximum)
        {
            var value = configuration.GetValue<long?>(key) ?? fallback;
            return Math.Clamp(value, minimum, maximum);
        }

        var logMaxFile = Read("School:Logs:MaxFileBytes", 10 * 1024 * 1024, 1024 * 1024, 1024L * 1024 * 1024);
        var logMaxTotal = Math.Clamp(configuration.GetValue<long?>("School:Logs:MaxTotalBytes") ?? Math.Max(200 * 1024 * 1024, logMaxFile),
            logMaxFile, 10L * 1024 * 1024 * 1024);
        return new StorageOptions
        {
            LogRetentionDays = (int)Read("School:Logs:RetentionDays", 14, 1, 3650),
            LogMaxFileBytes = logMaxFile,
            LogMaxTotalBytes = logMaxTotal,
            BackupRetentionCount = (int)Read("School:Backups:RetentionCount", 20, 1, 1000),
            BackupMaxTotalBytes = Read("School:Backups:MaxTotalBytes", 5L * 1024 * 1024 * 1024, 10 * 1024 * 1024, 1024L * 1024 * 1024 * 1024),
            BackupMinimumFreeBytes = Read("School:Backups:MinimumFreeBytes", 256L * 1024 * 1024, 0, 100L * 1024 * 1024 * 1024),
            PreMigrationBackupCount = (int)Read("School:Migrations:PreMigrationBackupCount", 5, 1, 100)
        };
    }
}
