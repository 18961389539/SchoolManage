using Microsoft.EntityFrameworkCore;

namespace School.Training;

public partial class SchoolDbContext
{
    public DbSet<JobPosition> JobPositions => Set<JobPosition>();
    public DbSet<JobSkill> JobSkills => Set<JobSkill>();
    public DbSet<JobQuestion> JobQuestions => Set<JobQuestion>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<JobAnswer> JobAnswers => Set<JobAnswer>();

    partial void ConfigureCamp(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobPosition>().HasIndex(x => x.Name).IsUnique().HasDatabaseName("IX_JobPositions_Name");
        modelBuilder.Entity<JobPosition>().HasIndex(x => x.StartupNodeId).HasDatabaseName("IX_JobPositions_StartupNodeId");
        modelBuilder.Entity<JobPosition>().HasOne<TrainingNode>().WithMany().HasForeignKey(x => x.StartupNodeId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<JobSkill>().HasIndex(x => new { x.PositionId, x.SortOrder }).HasDatabaseName("IX_JobSkills_PositionId_SortOrder");
        modelBuilder.Entity<JobSkill>().HasOne<JobPosition>().WithMany().HasForeignKey(x => x.PositionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<JobQuestion>().HasIndex(x => new { x.PositionId, x.SortOrder }).HasDatabaseName("IX_JobQuestions_PositionId_SortOrder");
        modelBuilder.Entity<JobQuestion>().HasIndex(x => x.SkillId).HasDatabaseName("IX_JobQuestions_SkillId");
        modelBuilder.Entity<JobQuestion>().HasOne<JobPosition>().WithMany().HasForeignKey(x => x.PositionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<JobQuestion>().HasOne<JobSkill>().WithMany().HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<JobAttempt>().HasIndex(x => new { x.PositionId, x.UserId }).IsUnique().HasDatabaseName("IX_JobAttempts_PositionId_UserId");
        modelBuilder.Entity<JobAttempt>().HasIndex(x => x.UserId).HasDatabaseName("IX_JobAttempts_UserId");
        modelBuilder.Entity<JobAttempt>().HasIndex(x => x.Status).HasDatabaseName("IX_JobAttempts_Status");
        modelBuilder.Entity<JobAttempt>().HasIndex(x => x.VirtualMachineId).HasDatabaseName("IX_JobAttempts_VirtualMachineId");
        modelBuilder.Entity<JobAttempt>().HasOne<JobPosition>().WithMany().HasForeignKey(x => x.PositionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<JobAttempt>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<JobAttempt>().HasOne<TrainingVirtualMachine>().WithMany().HasForeignKey(x => x.VirtualMachineId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<JobAnswer>().HasIndex(x => new { x.AttemptId, x.QuestionId }).IsUnique().HasDatabaseName("IX_JobAnswers_AttemptId_QuestionId");
        modelBuilder.Entity<JobAnswer>().HasIndex(x => x.QuestionId).HasDatabaseName("IX_JobAnswers_QuestionId");
        modelBuilder.Entity<JobAnswer>().HasOne<JobAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<JobAnswer>().HasOne<JobQuestion>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class JobPosition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int DurationMinutes { get; set; } = 60;
    public string? StartupNodeId { get; set; }
    public bool Published { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class JobSkill
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PositionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SubName { get; set; } = "";
    public string Ability { get; set; } = "";
    public int SortOrder { get; set; }
}

public class JobQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PositionId { get; set; } = "";
    public string? SkillId { get; set; }
    public string Type { get; set; } = "single";
    public string Stem { get; set; } = "";
    public string OptionsJson { get; set; } = "[]";
    public string AnswerJson { get; set; } = "[]";
    public string Analysis { get; set; } = "";
    public string RubricJson { get; set; } = "[]";
    public string PracticalRubricJson { get; set; } = "[]";
    public int Score { get; set; } = 10;
    public int SortOrder { get; set; }
}

public class JobAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PositionId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string StudentName { get; set; } = "";
    public string StudentUserName { get; set; } = "";
    public string StudentNumber { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string? VirtualMachineId { get; set; }
    public string EnvironmentJson { get; set; } = "[]";
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
}

public class JobAnswer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AttemptId { get; set; } = "";
    public string QuestionId { get; set; } = "";
    public string SelectedJson { get; set; } = "[]";
    public string TextAnswer { get; set; } = "";
    public string PracticalDetailsJson { get; set; } = "[]";
    public int ScoreAwarded { get; set; }
    public DateTime? AnsweredAt { get; set; }
}

public sealed record CampSkillInput(string Name, string? SubName = null, string? Ability = null);
public sealed record CampRubricItemInput(string Item, string Keyword, int Score);
public sealed record CampQuestionInput(
    string Type, string Stem, string[]? Options, int[]? Answers, int Score = 10,
    string? Analysis = null, int? SkillIndex = null, CampRubricItemInput[]? Rubric = null, CaseRubricItem[]? PracticalRubric = null);
public sealed record CampPositionInput(
    string Name, string? Description, int DurationMinutes, string? StartupNodeId = null,
    CampSkillInput[]? Skills = null, CampQuestionInput[]? Questions = null);
public sealed record CampPublishInput(bool Published);
public sealed record CampSaveAnswersInput(CampAnswerInput[]? Answers);
public sealed record CampAnswerInput(string QuestionId, int[]? Selected, string? Text = null);
public sealed record CampRubricItem(string Item, string Keyword, int Score);
