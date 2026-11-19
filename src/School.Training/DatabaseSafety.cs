using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.AspNetCore.DataProtection;

namespace School.Training;

public static class DatabaseSafety
{
    public const int ApplicationId = 0x5354524E;
    public const int SchemaVersion = 4;

    public static async Task ValidateBackup(string path, string keysDirectory)
    {
        await Validate(path, allowOlderSchema: true);
        if (!Directory.Exists(keysDirectory) || !Directory.EnumerateFiles(keysDirectory, "key-*.xml").Any()) throw new InvalidDataException("备份缺少DataProtection密钥文件");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keysDirectory), options =>
        {
            options.SetApplicationName("SchoolTraining").DisableAutomaticKeyGeneration();
            if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi(protectToLocalMachine: true);
        });
        var protector = provider.CreateProtector("EquipmentCredentials.v1");
        try
        {
            var probe = protector.Protect("backup-validation-probe");
            if (protector.Unprotect(probe) != "backup-validation-probe") throw new InvalidDataException("密钥验证失败");
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            await using var db = new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(cs).Options);
            var values = await db.Cabinets.Where(x => x.JumpPasswordProtected != "").Select(x => x.JumpPasswordProtected).ToListAsync();
            values.AddRange(await db.Devices.Where(x => x.PasswordProtected != "").Select(x => x.PasswordProtected).ToListAsync());
            values.AddRange(await db.Nodes.Where(x => x.PasswordProtected != "").Select(x => x.PasswordProtected).ToListAsync());
            foreach (var value in values) _ = protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException ex) { throw new InvalidDataException("备份密钥无法在当前Windows上解密已有凭据，禁止恢复", ex); }
    }

    /// <summary>
    /// Reads the product marker and structural version without requiring the current
    /// schema. Used before an upgrade so an older supported database can be migrated.
    /// </summary>
    public static async Task<int> Inspect(string path, bool fullIntegrity = false)
    {
        if (!File.Exists(path)) throw new InvalidDataException("数据库文件不存在");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString); await connection.OpenAsync();
        async Task<long> Pragma(string name) { using var cmd = connection.CreateCommand(); cmd.CommandText = "PRAGMA " + name; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
        if (await Pragma("application_id") != ApplicationId) throw new InvalidDataException("数据库不是本产品数据，产品标记不匹配");
        var version = checked((int)await Pragma("user_version"));
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = fullIntegrity ? "PRAGMA integrity_check" : "PRAGMA quick_check";
            var result = Convert.ToString(await cmd.ExecuteScalarAsync()); if (result != "ok") throw new InvalidDataException("SQLite完整性检查失败：" + result);
        }
        return version;
    }

    public static async Task Validate(string path, bool fullIntegrity = true, bool allowOlderSchema = false)
    {
        var version = await Inspect(path, fullIntegrity);
        if ((!allowOlderSchema && version != SchemaVersion) || (allowOlderSchema && (version < 1 || version > SchemaVersion)))
            throw new InvalidDataException("数据库结构版本不受当前程序支持，请使用对应版本或迁移工具");

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString); await connection.OpenAsync();
        await using var db = new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(connectionString).Options);
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!; var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            using var cmd = connection.CreateCommand(); cmd.CommandText = "PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\")";
            using var reader = await cmd.ExecuteReaderAsync(); var columns = new HashSet<string>(); while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            foreach (var property in entity.GetProperties()) if (!columns.Contains(property.GetColumnName(store)!)) throw new InvalidDataException($"数据库缺少产品表或字段：{table}.{property.GetColumnName(store)}");

            if (version != SchemaVersion) continue;
            foreach (var index in entity.GetIndexes())
            {
                var indexName = index.GetDatabaseName();
                if (string.IsNullOrEmpty(indexName)) continue;
                using var indexCommand = connection.CreateCommand();
                indexCommand.CommandText = "PRAGMA index_list(\"" + table.Replace("\"", "\"\"") + "\")";
                using var indexReader = await indexCommand.ExecuteReaderAsync();
                var found = false; var unique = false;
                while (await indexReader.ReadAsync())
                {
                    if (!string.Equals(indexReader.GetString(1), indexName, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true; unique = indexReader.GetInt32(2) != 0; break;
                }
                if (!found || unique != index.IsUnique) throw new InvalidDataException($"数据库缺少产品索引或唯一性不匹配：{table}.{indexName}");
            }
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check"; using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync()) throw new InvalidDataException("数据库关联完整性检查失败");
        }
    }
}
