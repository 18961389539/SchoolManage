using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using School.Training;

namespace School.Training.Tests;

public class ImportContractTests
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    static string[] Keys(JsonElement element)
        => element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Preview_response_keeps_its_wire_shape()
    {
        var response = new ImportPreviewResponse(
            "batch-1",
            [
                new ImportRow
                {
                    RowNumber = 2,
                    Data = new ImportData("张三", "zhangsan", "S001", "网络一班", "男", "13800000000", "a@b.cn"),
                    Errors = [new ImportError("账号", "必填")]
                }
            ],
            ValidCount: 0,
            ErrorCount: 1);

        var json = JsonSerializer.SerializeToElement(response, Options);

        Assert.Equal(["batchId", "errorCount", "rows", "validCount"], Keys(json));
        var row = json.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal(["data", "errors", "rowNumber"], Keys(row));
        Assert.Equal(
            ["className", "email", "gender", "name", "number", "phone", "userName"],
            Keys(row.GetProperty("data")));
        Assert.Equal(["column", "message"], Keys(row.GetProperty("errors").EnumerateArray().Single()));
        Assert.Equal(JsonValueKind.Number, json.GetProperty("validCount").ValueKind);
    }

    [Fact]
    public void Rows_page_keeps_its_wire_shape()
    {
        var response = new ImportRowsResponse([], TotalCount: 0, Page: 1, PageSize: 50);
        var json = JsonSerializer.SerializeToElement(response, Options);
        Assert.Equal(["page", "pageSize", "rows", "totalCount"], Keys(json));
        Assert.Equal(JsonValueKind.Array, json.GetProperty("rows").ValueKind);
    }

    [Fact]
    public void Hash_parallelism_leaves_one_core_for_the_host()
        => Assert.Equal(Math.Max(1, Environment.ProcessorCount - 1), ImportHashing.Parallelism);

    [Fact]
    public void Parallel_password_hashes_verify_with_the_configured_iteration_count()
    {
        var users = new AppUser[4];
        var passwords = new string[4];
        var hashes = new string[4];
        Parallel.For(0, users.Length, new ParallelOptions { MaxDegreeOfParallelism = ImportHashing.Parallelism },
            ImportHashing.Create, (index, _, hasher) =>
            {
                var user = new AppUser { UserName = "hash" + index, DisplayName = "哈希" };
                var password = ImportPasswordReceipts.GeneratePassword();
                users[index] = user;
                passwords[index] = password;
                hashes[index] = hasher.HashPassword(user, password);
                return hasher;
            }, static _ => { });

        var check = ImportHashing.Create();
        for (var i = 0; i < users.Length; i++)
            Assert.Equal(PasswordVerificationResult.Success, check.VerifyHashedPassword(users[i], hashes[i], passwords[i]));
    }

    [Fact]
    public void Status_response_keeps_its_wire_shape()
    {
        var response = new ImportStatusResponse(
            "batch-1", "running", 1, 2, 0, 0, "", DateTime.UnixEpoch, null, null, null, "正在创建账号");

        var json = JsonSerializer.SerializeToElement(response, Options);

        Assert.Equal(
            [
                "batchId", "completedAt", "createdAt", "failureMessage", "importedCount", "message",
                "processedCount", "queuedAt", "removedCount", "startedAt", "status", "totalCount"
            ],
            Keys(json));
        Assert.Equal(JsonValueKind.String, json.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("queuedAt").ValueKind);
    }
}
