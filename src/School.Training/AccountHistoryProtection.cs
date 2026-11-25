using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed record AccountHistoryCounts(int LearningRecords, int CaseSessions, int ExamAttempts, int CampAttempts)
{
    public bool HasHistory => LearningRecords + CaseSessions + ExamAttempts + CampAttempts > 0;
    public string Message => $"不能删除：所选账号有关联历史（学习记录 {LearningRecords} 条、课堂实操会话 {CaseSessions} 条、考试答卷 {ExamAttempts} 份、岗位测评 {CampAttempts} 次）。为保留历史数据，本次整批未删除。请保留账号并联系管理员处理，不要删除课堂或成绩来绕过保护。";
}

public static class AccountHistoryProtection
{
    public static async Task<AccountHistoryCounts> CountAsync(SchoolDbContext db, string[] userIds, CancellationToken ct)
        => new(
            await db.LearningRecords.CountAsync(item => userIds.Contains(item.UserId), ct),
            await db.ClassroomCaseSessions.CountAsync(item => userIds.Contains(item.UserId), ct),
            await db.ExamAttempts.CountAsync(item => userIds.Contains(item.UserId), ct),
            await db.JobAttempts.CountAsync(item => userIds.Contains(item.UserId), ct));
}
