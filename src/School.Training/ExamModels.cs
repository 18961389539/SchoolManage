using Microsoft.EntityFrameworkCore;

namespace School.Training;

public partial class SchoolDbContext
{
    public DbSet<ExamQuestion> ExamQuestions => Set<ExamQuestion>();
    public DbSet<ExamQuestionBank> ExamQuestionBanks => Set<ExamQuestionBank>();
    public DbSet<ExamPaper> ExamPapers => Set<ExamPaper>();
    public DbSet<ExamPaperQuestion> ExamPaperQuestions => Set<ExamPaperQuestion>();
    public DbSet<TrainingExam> TrainingExams => Set<TrainingExam>();
    public DbSet<ExamStudent> ExamStudents => Set<ExamStudent>();
    public DbSet<ExamAttempt> ExamAttempts => Set<ExamAttempt>();
    public DbSet<ExamAnswer> ExamAnswers => Set<ExamAnswer>();
    public DbSet<ExamPracticalEnvironment> ExamPracticalEnvironments => Set<ExamPracticalEnvironment>();

    partial void ConfigureExams(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExamQuestion>().HasIndex(x => x.Type).HasDatabaseName("IX_ExamQuestions_Type");
        modelBuilder.Entity<ExamQuestion>().HasIndex(x => x.CreatedAt).HasDatabaseName("IX_ExamQuestions_CreatedAt");
        modelBuilder.Entity<ExamQuestionBank>().Property(x => x.Name).UseCollation("NOCASE");
        modelBuilder.Entity<ExamQuestionBank>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<ExamQuestion>().HasIndex(x => new { x.BankId, x.Type });
        modelBuilder.Entity<ExamQuestion>().HasOne<ExamQuestionBank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExamPaper>().HasIndex(x => x.Name).IsUnique().HasDatabaseName("IX_ExamPapers_Name");
        modelBuilder.Entity<ExamPaperQuestion>().HasIndex(x => new { x.PaperId, x.QuestionId }).IsUnique().HasDatabaseName("IX_ExamPaperQuestions_PaperId_QuestionId");
        modelBuilder.Entity<ExamPaperQuestion>().HasIndex(x => new { x.PaperId, x.SortOrder }).HasDatabaseName("IX_ExamPaperQuestions_PaperId_SortOrder");
        modelBuilder.Entity<ExamPaperQuestion>().HasIndex(x => x.QuestionId).HasDatabaseName("IX_ExamPaperQuestions_QuestionId");
        modelBuilder.Entity<ExamPaperQuestion>().HasOne<ExamPaper>().WithMany().HasForeignKey(x => x.PaperId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamPaperQuestion>().HasOne<ExamQuestion>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TrainingExam>().HasIndex(x => x.Name).IsUnique().HasDatabaseName("IX_Exams_Name");
        modelBuilder.Entity<TrainingExam>().HasIndex(x => new { x.Status, x.StartAt }).HasDatabaseName("IX_Exams_Status_StartAt");
        modelBuilder.Entity<TrainingExam>().HasIndex(x => x.PaperId).HasDatabaseName("IX_Exams_PaperId");
        modelBuilder.Entity<TrainingExam>().HasIndex(x => x.CreatedByUserId).HasDatabaseName("IX_Exams_CreatedByUserId");
        modelBuilder.Entity<TrainingExam>().HasOne<ExamPaper>().WithMany().HasForeignKey(x => x.PaperId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TrainingExam>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExamStudent>().HasIndex(x => new { x.ExamId, x.StudentUserId }).IsUnique().HasDatabaseName("IX_ExamStudents_ExamId_StudentUserId");
        modelBuilder.Entity<ExamStudent>().HasIndex(x => x.StudentUserId).HasDatabaseName("IX_ExamStudents_StudentUserId");
        modelBuilder.Entity<ExamStudent>().HasOne<TrainingExam>().WithMany().HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamStudent>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.StudentUserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamStudent>().HasOne<SchoolClass>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ExamAttempt>().HasIndex(x => new { x.ExamId, x.UserId }).IsUnique().HasDatabaseName("IX_ExamAttempts_ExamId_UserId");
        modelBuilder.Entity<ExamAttempt>().HasIndex(x => x.UserId).HasDatabaseName("IX_ExamAttempts_UserId");
        modelBuilder.Entity<ExamAttempt>().HasIndex(x => x.Status).HasDatabaseName("IX_ExamAttempts_Status");
        modelBuilder.Entity<ExamAttempt>().HasOne<TrainingExam>().WithMany().HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamAttempt>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamAnswer>().HasIndex(x => new { x.AttemptId, x.PaperQuestionId }).IsUnique().HasDatabaseName("IX_ExamAnswers_AttemptId_PaperQuestionId");
        modelBuilder.Entity<ExamAnswer>().HasIndex(x => x.PaperQuestionId).HasDatabaseName("IX_ExamAnswers_PaperQuestionId");
        modelBuilder.Entity<ExamAnswer>().HasOne<ExamAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamAnswer>().HasOne<ExamPaperQuestion>().WithMany().HasForeignKey(x => x.PaperQuestionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamPracticalEnvironment>().HasKey(x => new { x.EnrollmentId, x.PaperQuestionId });
        modelBuilder.Entity<ExamPracticalEnvironment>().HasIndex(x => x.VmUuid).IsUnique();
        modelBuilder.Entity<ExamPracticalEnvironment>().HasOne<ExamStudent>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamPracticalEnvironment>().HasOne<ExamPaperQuestion>().WithMany().HasForeignKey(x => x.PaperQuestionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ExamQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BankId { get; set; } = ExamQuestionBank.DefaultId;
    public string Type { get; set; } = "single";
    public string Stem { get; set; } = "";
    public string OptionsJson { get; set; } = "[]";
    public string AnswerJson { get; set; } = "[]";
    public string Analysis { get; set; } = "";
    public string PracticalNodeId { get; set; } = "";
    public string PracticalRubricJson { get; set; } = "[]";
    public string PracticalResources { get; set; } = "";
    public int Score { get; set; } = 10;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ExamQuestionBank
{
    public const string DefaultId = "default";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Type { get; set; } = "mixed";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ExamPaper
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A question placed on a paper. Content is copied from the bank when added so later
/// edits to the bank do not rewrite papers that exams already reference.
/// </summary>
public class ExamPaperQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PaperId { get; set; } = "";
    public string QuestionId { get; set; } = "";
    public string Type { get; set; } = "single";
    public string Stem { get; set; } = "";
    public string OptionsJson { get; set; } = "[]";
    public string AnswerJson { get; set; } = "[]";
    public string Analysis { get; set; } = "";
    public string PracticalNodeId { get; set; } = "";
    public string PracticalRubricJson { get; set; } = "[]";
    public string PracticalResources { get; set; } = "";
    public int Score { get; set; } = 10;
    public int SortOrder { get; set; }
}

public class TrainingExam
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string PaperId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime StartAt { get; set; } = DateTime.UtcNow;
    public DateTime EndAt { get; set; } = DateTime.UtcNow.AddDays(1);
    public int DurationMinutes { get; set; } = 60;
    public string Status { get; set; } = "scheduled";
    public string CreatedByUserId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ExamStudent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ExamId { get; set; } = "";
    public string StudentUserId { get; set; } = "";
    public string StudentPersonId { get; set; } = "";
    public string? ClassId { get; set; }
    public string ClassName { get; set; } = "";
    public string DeploymentStatus { get; set; } = "pending";
    public string DeploymentMessage { get; set; } = "";
    public DateTime? DeployedAt { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class ExamAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ExamId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string StudentName { get; set; } = "";
    public string StudentUserName { get; set; } = "";
    public string StudentNumber { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Status { get; set; } = "inProgress";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime Deadline { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public bool AutoSubmitted { get; set; }
    public int ObjectiveScore { get; set; }
    public int PracticalScore { get; set; }
    public int TotalScore { get; set; }
    public int MaxScore { get; set; }
    public DateTime? GradedAt { get; set; }
    public string GradedByUserId { get; set; } = "";
}

public class ExamAnswer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AttemptId { get; set; } = "";
    public string PaperQuestionId { get; set; } = "";
    public string SelectedJson { get; set; } = "[]";
    public string TextAnswer { get; set; } = "";
    public int? ScoreAwarded { get; set; }
    public string Comment { get; set; } = "";
    public string PracticalDetailsJson { get; set; } = "[]";
    public DateTime? AnsweredAt { get; set; }
}

public sealed class ExamPracticalEnvironment
{
    public string EnrollmentId { get; set; } = "";
    public string PaperQuestionId { get; set; } = "";
    public string BaselineJson { get; set; } = "[]";
    public string ConfigurationJson { get; set; } = "[]";
    public int Revision { get; set; }
    public bool HasWork { get; set; }
    public DateTime DeployedAt { get; set; }
    public DateTime? RestartedAt { get; set; }
    public string? VmUuid { get; set; }
    public string DeploymentStatus { get; set; } = "deployed";
    public string DeploymentMessage { get; set; } = "";
}

public record ExamQuestionInput(string Type, string Stem, string[]? Options, int[]? Answers, int Score = 10,
    string? Analysis = null, string? PracticalNodeId = null, CaseRubricItem[]? PracticalRubric = null, string? BankId = null,
    string? PracticalResources = null);
public record ExamQuestionBankInput(string Name, string Type);
public record ExamDeviceUpdate(int Revision, SimulatedDeviceState Configuration);
public record ExamPaperInput(string Name, string? Description, string? Remark);
public record TrainingExamInput(
    string Name, string PaperId, string? Description, DateTime StartAt, DateTime EndAt,
    int DurationMinutes, string[]? ClassIds = null, string[]? ProfessionIds = null);
public record ExamStudentAdjustInput(string[]? ClassIds = null, string[]? ProfessionIds = null, string[]? StudentUserIds = null);
public record ExamPaperQuestionInput(string QuestionId, int? Score = null);
public record ExamPaperQuestionAddInput(ExamPaperQuestionInput[]? Items);
public record ExamPaperQuestionScoreInput(int Score);
public record ExamSubmitInput(ExamSubmitAnswer[]? Answers);
public record ExamSaveAnswersInput(ExamSubmitAnswer[]? Answers);
public record ExamSubmitAnswer(string PaperQuestionId, int[]? Selected, string? Text = null);
public record ExamGradeInput(ExamGradeAnswer[]? Answers);
public record ExamGradeAnswer(string PaperQuestionId, int Score, string? Comment = null);
public record ExamDeploymentInput(string[]? StudentUserIds = null);
public record IdListInput(string[]? Ids);
