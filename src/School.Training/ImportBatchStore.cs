using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace School.Training;

public static class PasswordHashing
{
    public const int IterationCount = 600_000;
}

public static class ImportHashing
{
    public static int Parallelism => Math.Max(1, Environment.ProcessorCount - 1);

    public static PasswordHasher Create() => new();

    public sealed class PasswordHasher : Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>
    {
        public PasswordHasher()
            : base(Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Identity.PasswordHasherOptions
            {
                IterationCount = PasswordHashing.IterationCount
            }))
        {
        }
    }
}

/// <summary>
/// Preview lines live in <see cref="ImportBatchRow"/> so a page is a range read and the worker
/// never rewrites one large JSON document. <see cref="ImportBatch.RowsJson"/> stays <c>[]</c> for
/// new batches; a queued batch created before this table is still readable from that column.
/// </summary>
public static class ImportBatchStore
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static int ClampPageSize(int? pageSize) => Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

    public static async Task InsertAsync(SchoolDbContext db, string batchId, IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var connection = Open(db);
        var transaction = CurrentTransaction(db);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "ImportBatchRows" ("Id", "BatchId", "RowNumber", "PayloadJson", "HasError")
            VALUES ($id, $batchId, $rowNumber, $payload, $hasError)
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var batch = command.Parameters.Add("$batchId", SqliteType.Text);
        var rowNumber = command.Parameters.Add("$rowNumber", SqliteType.Integer);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);
        var hasError = command.Parameters.Add("$hasError", SqliteType.Integer);
        batch.Value = batchId;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = Guid.NewGuid().ToString("N");
            rowNumber.Value = row.RowNumber;
            payload.Value = JsonSerializer.Serialize(row, JsonOptions);
            hasError.Value = row.Errors.Count > 0 ? 1 : 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task KeepValidAsync(SchoolDbContext db, string batchId, IReadOnlyList<ImportRow> all, IReadOnlyList<ImportRow> valid, CancellationToken cancellationToken)
    {
        var stored = await db.ImportBatchRows.AnyAsync(x => x.BatchId == batchId, cancellationToken);
        if (!stored)
        {
            await InsertAsync(db, batchId, valid, cancellationToken);
            return;
        }
        var keep = valid.Select(row => row.RowNumber).ToHashSet();
        foreach (var chunk in all.Where(row => !keep.Contains(row.RowNumber)).Select(row => row.RowNumber).Chunk(400))
        {
            var numbers = chunk;
            await db.ImportBatchRows.Where(x => x.BatchId == batchId && numbers.Contains(x.RowNumber)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    public static Task DeleteAsync(SchoolDbContext db, string batchId, CancellationToken cancellationToken)
        => db.ImportBatchRows.Where(x => x.BatchId == batchId).ExecuteDeleteAsync(cancellationToken);

    public static async Task<List<ImportRow>> LoadAsync(SchoolDbContext db, ImportBatch batch, CancellationToken cancellationToken)
    {
        var payloads = await db.ImportBatchRows.AsNoTracking()
            .Where(x => x.BatchId == batch.Id)
            .OrderBy(x => x.RowNumber)
            .Select(x => x.PayloadJson)
            .ToListAsync(cancellationToken);
        if (payloads.Count > 0) return payloads.Select(Deserialize).ToList();
        return DeserializeList(batch.RowsJson);
    }

    public static async Task<ImportRowsResponse> PageAsync(SchoolDbContext db, ImportBatch batch, int page, int pageSize, string? filter, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = ClampPageSize(pageSize);
        var mode = filter is "invalid" or "valid" ? filter : "all";
        var stored = await db.ImportBatchRows.AsNoTracking().AnyAsync(x => x.BatchId == batch.Id, cancellationToken);
        if (!stored)
        {
            var legacy = DeserializeList(batch.RowsJson);
            IEnumerable<ImportRow> filtered = mode switch
            {
                "invalid" => legacy.Where(row => row.Errors.Count > 0),
                "valid" => legacy.Where(row => row.Errors.Count == 0),
                _ => legacy
            };
            var list = filtered.ToList();
            return new ImportRowsResponse(list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), list.Count, page, pageSize);
        }

        var query = db.ImportBatchRows.AsNoTracking().Where(x => x.BatchId == batch.Id);
        if (mode == "invalid") query = query.Where(x => x.HasError);
        else if (mode == "valid") query = query.Where(x => !x.HasError);
        var total = await query.CountAsync(cancellationToken);
        var payloads = await query.OrderBy(x => x.RowNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.PayloadJson)
            .ToListAsync(cancellationToken);
        return new ImportRowsResponse(payloads.Select(Deserialize).ToList(), total, page, pageSize);
    }

    /// <summary>
    /// Inserts accounts with a prepared statement inside the caller's transaction. Hashing stays
    /// outside that transaction, so the SQLite write lock covers only these inserts.
    /// </summary>
    public static async Task InsertAccountsAsync(SchoolDbContext db, IReadOnlyList<AppUser> users, IReadOnlyList<Person> people, CancellationToken cancellationToken)
    {
        if (users.Count != people.Count) throw new InvalidOperationException("导入账号与档案数量不一致");
        var connection = Open(db);
        var transaction = CurrentTransaction(db);
        await using var userCommand = connection.CreateCommand();
        userCommand.Transaction = transaction;
        userCommand.CommandText = """
            INSERT INTO "Users" ("Id", "UserName", "DisplayName", "PasswordHash", "Role", "SecurityStamp", "MustChangePassword", "CreatedAt")
            VALUES ($id, $userName, $displayName, $passwordHash, $role, $stamp, $mustChange, $createdAt)
            """;
        var userId = userCommand.Parameters.Add("$id", SqliteType.Text);
        var userName = userCommand.Parameters.Add("$userName", SqliteType.Text);
        var displayName = userCommand.Parameters.Add("$displayName", SqliteType.Text);
        var passwordHash = userCommand.Parameters.Add("$passwordHash", SqliteType.Text);
        var role = userCommand.Parameters.Add("$role", SqliteType.Text);
        var stamp = userCommand.Parameters.Add("$stamp", SqliteType.Text);
        var mustChange = userCommand.Parameters.Add("$mustChange", SqliteType.Integer);
        var createdAt = userCommand.Parameters.Add("$createdAt", SqliteType.Text);

        await using var personCommand = connection.CreateCommand();
        personCommand.Transaction = transaction;
        personCommand.CommandText = """
            INSERT INTO "People" ("Id", "UserId", "Kind", "Name", "Number", "ClassId", "Gender", "Phone", "Email")
            VALUES ($id, $userId, $kind, $name, $number, $classId, $gender, $phone, $email)
            """;
        var personId = personCommand.Parameters.Add("$id", SqliteType.Text);
        var ownerId = personCommand.Parameters.Add("$userId", SqliteType.Text);
        var kind = personCommand.Parameters.Add("$kind", SqliteType.Text);
        var name = personCommand.Parameters.Add("$name", SqliteType.Text);
        var number = personCommand.Parameters.Add("$number", SqliteType.Text);
        var classId = personCommand.Parameters.Add("$classId", SqliteType.Text);
        var gender = personCommand.Parameters.Add("$gender", SqliteType.Text);
        var phone = personCommand.Parameters.Add("$phone", SqliteType.Text);
        var email = personCommand.Parameters.Add("$email", SqliteType.Text);

        for (var i = 0; i < users.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var user = users[i];
            var person = people[i];
            userId.Value = user.Id;
            userName.Value = user.UserName;
            displayName.Value = user.DisplayName;
            passwordHash.Value = user.PasswordHash;
            role.Value = user.Role;
            stamp.Value = user.SecurityStamp;
            mustChange.Value = user.MustChangePassword ? 1 : 0;
            createdAt.Value = FormatTimestamp(user.CreatedAt);
            await userCommand.ExecuteNonQueryAsync(cancellationToken);

            personId.Value = person.Id;
            ownerId.Value = person.UserId;
            kind.Value = person.Kind;
            name.Value = person.Name;
            number.Value = person.Number;
            classId.Value = string.IsNullOrEmpty(person.ClassId) ? DBNull.Value : person.ClassId;
            gender.Value = person.Gender;
            phone.Value = person.Phone;
            email.Value = person.Email;
            await personCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    static SqliteConnection Open(SchoolDbContext db)
        => db.Database.GetDbConnection() as SqliteConnection ?? throw new InvalidOperationException("导入写入需要 SQLite 连接");

    static SqliteTransaction CurrentTransaction(SchoolDbContext db)
        => db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction
            ?? throw new InvalidOperationException("导入写入必须位于当前事务里");

    static string FormatTimestamp(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return utc.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
    }

    static List<ImportRow> DeserializeList(string json)
        => string.IsNullOrWhiteSpace(json) || json == "[]"
            ? []
            : JsonSerializer.Deserialize<List<ImportRow>>(json, JsonOptions) ?? throw new InvalidDataException("导入预览数据无法读取，请重新上传");

    static ImportRow Deserialize(string json)
        => JsonSerializer.Deserialize<ImportRow>(json, JsonOptions) ?? throw new InvalidDataException("导入预览数据无法读取，请重新上传");
}
