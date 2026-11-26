using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using School.Training;

namespace School.Training.Tests;

[Collection("http")]
public sealed class ImportJobTests(ApiScenario scenario) : IClassFixture<ApiScenario>
{
    [Fact]
    public async Task Preview_pages_rows_and_keeps_the_whole_file_in_the_row_table()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var csv = new StringBuilder("姓名,账号,学号,班级,性别,手机号,电子邮箱\r\n");
        for (var i = 0; i < 51; i++)
            csv.Append($"导入{i},imp{suffix}{i:00},N{suffix}{i:00},,,,\r\n");
        csv.Append($",bad{suffix},,,,,\r\n");

        var preview = await UploadAsync(csv.ToString(), pageSize: 50);
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        Assert.Equal(50, preview.Body.GetProperty("rows").GetArrayLength());
        Assert.Equal(51, preview.Body.GetProperty("validCount").GetInt32());
        Assert.Equal(1, preview.Body.GetProperty("errorCount").GetInt32());
        var batchId = preview.Body.GetProperty("batchId").GetString()!;

        var second = await Http.Get(scenario.Admin, $"/api/imports/{batchId}/rows?page=2&pageSize=50&filter=all");
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(52, second.Body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, second.Body.GetProperty("rows").GetArrayLength());

        var invalid = await Http.Get(scenario.Admin, $"/api/imports/{batchId}/rows?page=1&pageSize=50&filter=invalid");
        Assert.Equal(HttpStatusCode.OK, invalid.Status);
        Assert.Equal(1, invalid.Body.GetProperty("totalCount").GetInt32());
        Assert.True(invalid.Body.GetProperty("rows").EnumerateArray().Single().GetProperty("errors").GetArrayLength() > 0);

        await scenario.WithDb(async db =>
        {
            var batch = await db.ImportBatches.SingleAsync(x => x.Id == batchId);
            Assert.Equal("[]", batch.RowsJson);
            Assert.Equal(52, await db.ImportBatchRows.CountAsync(x => x.BatchId == batchId));
        });
    }

    [Fact]
    public async Task Commit_creates_accounts_with_one_time_passwords_and_clears_preview_rows()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var account = "stu" + suffix;
        var csv = $"姓名,账号,学号,班级,性别,手机号,电子邮箱\r\n导入学生,{account},S{suffix},,,,\r\n";
        var preview = await UploadAsync(csv, pageSize: 50);
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        var batchId = preview.Body.GetProperty("batchId").GetString()!;

        var started = await Http.Post(scenario.Admin, $"/api/imports/{batchId}/commit", new { removeInvalidRows = false }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.Accepted, started.Status);
        var completed = await WaitAsync(batchId);
        Assert.Equal("completed", completed.Body.GetProperty("status").GetString());
        Assert.Equal(1, completed.Body.GetProperty("importedCount").GetInt32());

        var receipt = await Http.Get(scenario.Admin, $"/api/imports/{batchId}/receipt");
        Assert.Equal(HttpStatusCode.OK, receipt.Status);
        var item = receipt.Body.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(account, item.GetProperty("account").GetString());
        var password = item.GetProperty("password").GetString()!;

        using var client = scenario.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Http.Post(client, "/api/auth/login", new { userName = account, password });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.True(login.Body.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        var blocked = await Http.Get(client, "/api/dashboard");
        Assert.Equal((HttpStatusCode)428, blocked.Status);

        await scenario.WithDb(async db =>
        {
            var user = await db.Users.SingleAsync(x => x.UserName == account);
            Assert.True(user.MustChangePassword);
            Assert.Equal("Student", user.Role);
            Assert.NotEqual(default, user.CreatedAt);
            var person = await db.People.SingleAsync(x => x.UserId == user.Id);
            Assert.Equal("S" + suffix, person.Number);
            Assert.Equal(0, await db.ImportBatchRows.CountAsync(x => x.BatchId == batchId));
        });
    }

    [Fact]
    public async Task Commit_imports_nothing_when_the_account_appears_after_preview()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var account = "dup" + suffix;
        var csv = $"姓名,账号,学号,班级,性别,手机号,电子邮箱\r\n重复学生,{account},D{suffix},,,,\r\n";
        var preview = await UploadAsync(csv, pageSize: 50);
        var batchId = preview.Body.GetProperty("batchId").GetString()!;
        var created = await Http.Post(scenario.Admin, "/api/people", new
        {
            kind = "student",
            name = "已存在学生",
            userName = account,
            number = "D" + suffix,
            role = "Student",
            password = "StudentPass2026"
        }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.OK, created.Status);

        var started = await Http.Post(scenario.Admin, $"/api/imports/{batchId}/commit", new { removeInvalidRows = false }, scenario.AdminCsrf);
        Assert.Equal(HttpStatusCode.BadRequest, started.Status);
        Assert.Contains("问题行", started.Message);
        await scenario.WithDb(async db =>
        {
            Assert.Equal(ImportBatchStatus.Preview, (await db.ImportBatches.SingleAsync(x => x.Id == batchId)).Status);
            Assert.Equal(1, await db.People.CountAsync(x => x.Number == "D" + suffix));
            Assert.Equal(1, await db.ImportBatchRows.CountAsync(x => x.BatchId == batchId));
        });
    }

    async Task<ApiResponse> UploadAsync(string csv, int pageSize)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "students.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/imports/preview?kind=student&pageSize={pageSize}") { Content = content };
        request.Headers.Add("X-CSRF-TOKEN", scenario.AdminCsrf);
        using var response = await scenario.Admin.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var document = System.Text.Json.JsonDocument.Parse(text);
        return new ApiResponse(response.StatusCode, document.RootElement.Clone());
    }

    async Task<ApiResponse> WaitAsync(string batchId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        ApiResponse last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await Http.Get(scenario.Admin, "/api/imports/" + batchId);
            var status = last.Body.GetProperty("status").GetString();
            if (status is "completed" or "failed" or "cancelled") return last;
            await Task.Delay(200);
        }
        return last;
    }
}
