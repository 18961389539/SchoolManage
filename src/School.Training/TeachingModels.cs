using Microsoft.EntityFrameworkCore;

namespace School.Training;

public partial class SchoolDbContext
{
    public DbSet<TrainingCourse> Courses => Set<TrainingCourse>();
    public DbSet<CoreCourseSeries> CoreCourseSeries => Set<CoreCourseSeries>();
    public DbSet<CaseTopologyTemplate> CaseTopologyTemplates => Set<CaseTopologyTemplate>();
    public DbSet<CourseChapter> CourseChapters => Set<CourseChapter>();
    public DbSet<CourseSection> CourseSections => Set<CourseSection>();
    public DbSet<CourseResource> CourseResources => Set<CourseResource>();
    public DbSet<CourseQuestion> CourseQuestions => Set<CourseQuestion>();
    public DbSet<CourseResourceFile> CourseResourceFiles => Set<CourseResourceFile>();
    public DbSet<Classroom> Classrooms => Set<Classroom>();
    public DbSet<ClassroomStudent> ClassroomStudents => Set<ClassroomStudent>();
    public DbSet<LearningRecord> LearningRecords => Set<LearningRecord>();
    public DbSet<PracticeAttempt> PracticeAttempts => Set<PracticeAttempt>();
    public DbSet<ClassroomGroup> ClassroomGroups => Set<ClassroomGroup>();
    public DbSet<ClassroomGroupMember> ClassroomGroupMembers => Set<ClassroomGroupMember>();
    public DbSet<ClassroomCabinetAssignment> ClassroomCabinetAssignments => Set<ClassroomCabinetAssignment>();
    public DbSet<ClassroomCaseSession> ClassroomCaseSessions => Set<ClassroomCaseSession>();
    public DbSet<ClassroomCaseGrade> ClassroomCaseGrades => Set<ClassroomCaseGrade>();
    public DbSet<CaseSessionEvidence> CaseSessionEvidences => Set<CaseSessionEvidence>();

    partial void ConfigureTeaching(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrainingCourse>().HasIndex(x => x.Name).IsUnique().HasDatabaseName("IX_Courses_Name");
        modelBuilder.Entity<TrainingCourse>().HasIndex(x => new { x.Status, x.Type }).HasDatabaseName("IX_Courses_Status_Type");
        // Editing surface: the client sends back the version it loaded, and EF adds it to the
        // UPDATE's WHERE clause so a write built on stale content cannot land.
        modelBuilder.Entity<TrainingCourse>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<CourseChapter>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<CourseSection>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<CourseResource>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<CoreCourseSeries>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<CaseTopologyTemplate>().HasIndex(x => new { x.NodeId, x.Name }).IsUnique();
        modelBuilder.Entity<CaseTopologyTemplate>().HasOne<TrainingNode>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TrainingCourse>().HasOne<CoreCourseSeries>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CourseChapter>().HasIndex(x => new { x.CourseId, x.SortOrder }).HasDatabaseName("IX_CourseChapters_CourseId_SortOrder");
        modelBuilder.Entity<CourseChapter>().HasOne<TrainingCourse>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CourseSection>().HasIndex(x => new { x.ChapterId, x.SortOrder }).HasDatabaseName("IX_CourseSections_ChapterId_SortOrder");
        modelBuilder.Entity<CourseSection>().HasOne<CourseChapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CourseResource>().HasIndex(x => new { x.SectionId, x.SortOrder }).HasDatabaseName("IX_CourseResources_SectionId_SortOrder");
        modelBuilder.Entity<CourseResource>().HasIndex(x => x.StartupNodeId).HasDatabaseName("IX_CourseResources_StartupNodeId");
        modelBuilder.Entity<CourseResource>().HasOne<CourseSection>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CourseResource>().HasOne<TrainingNode>().WithMany().HasForeignKey(x => x.StartupNodeId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CourseQuestion>().HasIndex(x => new { x.ResourceId, x.SortOrder }).HasDatabaseName("IX_CourseQuestions_ResourceId_SortOrder");
        modelBuilder.Entity<CourseQuestion>().HasOne<CourseResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CourseResourceFile>().HasIndex(x => new { x.ResourceId, x.SortOrder }).HasDatabaseName("IX_CourseResourceFiles_ResourceId_SortOrder");
        modelBuilder.Entity<CourseResourceFile>().HasIndex(x => new { x.ResourceId, x.IsPinned }).HasDatabaseName("IX_CourseResourceFiles_ResourceId_IsPinned");
        modelBuilder.Entity<CourseResourceFile>().HasOne<CourseResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Classroom>().HasIndex(x => x.TeacherUserId).HasDatabaseName("IX_Classrooms_TeacherUserId");
        modelBuilder.Entity<Classroom>().HasIndex(x => x.CourseId).HasDatabaseName("IX_Classrooms_CourseId");
        modelBuilder.Entity<Classroom>().HasIndex(x => x.Status).HasDatabaseName("IX_Classrooms_Status");
        modelBuilder.Entity<Classroom>().HasOne<TrainingCourse>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Classroom>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.TeacherUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ClassroomStudent>().HasIndex(x => new { x.ClassroomId, x.StudentUserId }).IsUnique().HasDatabaseName("IX_ClassroomStudents_ClassroomId_StudentUserId");
        modelBuilder.Entity<ClassroomStudent>().HasIndex(x => x.StudentUserId).HasDatabaseName("IX_ClassroomStudents_StudentUserId");
        modelBuilder.Entity<ClassroomStudent>().HasOne<Classroom>().WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomStudent>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.StudentUserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LearningRecord>().HasIndex(x => new { x.ClassroomId, x.UserId, x.ResourceId }).IsUnique().HasDatabaseName("IX_LearningRecords_ClassroomId_UserId_ResourceId");
        modelBuilder.Entity<LearningRecord>().HasIndex(x => x.UserId).HasDatabaseName("IX_LearningRecords_UserId");
        modelBuilder.Entity<LearningRecord>().HasIndex(x => x.ResourceId).HasDatabaseName("IX_LearningRecords_ResourceId");
        modelBuilder.Entity<LearningRecord>().HasOne<Classroom>().WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LearningRecord>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LearningRecord>().HasOne<CourseResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PracticeAttempt>().HasIndex(x => new { x.LearningRecordId, x.SubmittedAt }).HasDatabaseName("IX_PracticeAttempts_LearningRecordId_SubmittedAt");
        modelBuilder.Entity<PracticeAttempt>().HasOne<LearningRecord>().WithMany().HasForeignKey(x => x.LearningRecordId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomGroup>().HasIndex(x => new { x.ClassroomId, x.Name }).IsUnique().HasDatabaseName("IX_ClassroomGroups_ClassroomId_Name");
        modelBuilder.Entity<ClassroomGroup>().HasOne<Classroom>().WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomGroupMember>().HasIndex(x => new { x.GroupId, x.StudentUserId }).IsUnique().HasDatabaseName("IX_ClassroomGroupMembers_GroupId_StudentUserId");
        modelBuilder.Entity<ClassroomGroupMember>().HasIndex(x => x.StudentUserId).HasDatabaseName("IX_ClassroomGroupMembers_StudentUserId");
        modelBuilder.Entity<ClassroomGroupMember>().HasOne<ClassroomGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomGroupMember>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.StudentUserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomCabinetAssignment>().HasIndex(x => x.CabinetId).IsUnique().HasDatabaseName("IX_ClassroomCabinetAssignments_CabinetId");
        modelBuilder.Entity<ClassroomCabinetAssignment>().HasIndex(x => x.ClassroomId).HasDatabaseName("IX_ClassroomCabinetAssignments_ClassroomId");
        modelBuilder.Entity<ClassroomCabinetAssignment>().HasIndex(x => x.GroupId).HasDatabaseName("IX_ClassroomCabinetAssignments_GroupId");
        modelBuilder.Entity<ClassroomCabinetAssignment>().HasIndex(x => x.ResourceId).HasDatabaseName("IX_ClassroomCabinetAssignments_ResourceId");
        modelBuilder.Entity<ClassroomCabinetAssignment>().HasOne<Classroom>().WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomCabinetAssignment>().HasOne<ClassroomGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomCabinetAssignment>().HasOne<Cabinet>().WithMany().HasForeignKey(x => x.CabinetId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ClassroomCabinetAssignment>().HasOne<CourseResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomCaseSession>().HasIndex(x => x.LearningRecordId).IsUnique().HasDatabaseName("IX_ClassroomCaseSessions_LearningRecordId");
        modelBuilder.Entity<ClassroomCaseSession>().HasIndex(x => new { x.ClassroomId, x.Status }).HasDatabaseName("IX_ClassroomCaseSessions_ClassroomId_Status");
        modelBuilder.Entity<ClassroomCaseSession>().HasIndex(x => x.UserId).HasDatabaseName("IX_ClassroomCaseSessions_UserId");
        modelBuilder.Entity<ClassroomCaseSession>().HasIndex(x => x.ResourceId).HasDatabaseName("IX_ClassroomCaseSessions_ResourceId");
        modelBuilder.Entity<ClassroomCaseSession>().HasIndex(x => x.NodeId).HasDatabaseName("IX_ClassroomCaseSessions_NodeId");
        modelBuilder.Entity<ClassroomCaseSession>().HasIndex(x => x.VirtualMachineId).HasDatabaseName("IX_ClassroomCaseSessions_VirtualMachineId");
        modelBuilder.Entity<ClassroomCaseSession>().HasOne<Classroom>().WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomCaseSession>().HasOne<LearningRecord>().WithMany().HasForeignKey(x => x.LearningRecordId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomCaseSession>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomCaseSession>().HasOne<CourseResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ClassroomCaseSession>().HasOne<TrainingNode>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ClassroomCaseSession>().HasOne<TrainingVirtualMachine>().WithMany().HasForeignKey(x => x.VirtualMachineId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ClassroomCaseGrade>().HasIndex(x => new { x.SessionId, x.ScoredAt });
        modelBuilder.Entity<ClassroomCaseGrade>().HasOne<ClassroomCaseSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CaseSessionEvidence>().HasIndex(x => new { x.SessionId, x.CreatedAt }).HasDatabaseName("IX_CaseSessionEvidences_SessionId_CreatedAt");
        modelBuilder.Entity<CaseSessionEvidence>().HasOne<ClassroomCaseSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TrainingCourse
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "general";
    public string Status { get; set; } = "draft";
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public bool IsActive { get; set; }
    public string? SeriesId { get; set; }
    public int Version { get; set; }
}

public class CoreCourseSeries
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public class CourseChapter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CourseId { get; set; } = "";
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public int Version { get; set; }
}

public class CourseSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ChapterId { get; set; } = "";
    public string Name { get; set; } = "";
    public int DurationMinutes { get; set; } = 30;
    public int SortOrder { get; set; }
    public int Version { get; set; }
}

public class CourseResource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "theory";
    public int DurationMinutes { get; set; } = 15;
    public string Content { get; set; } = "";
    public string Topology { get; set; } = "";
    public string TopologyBlueprintJson { get; set; } = "null";
    public string CaseRubricJson { get; set; } = "[]";
    public string? StartupNodeId { get; set; }
    public int SortOrder { get; set; }
    public int Version { get; set; }
}

public class CourseQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ResourceId { get; set; } = "";
    public string Type { get; set; } = "single";
    public string Stem { get; set; } = "";
    public string OptionsJson { get; set; } = "[]";
    public string AnswerJson { get; set; } = "[]";
    public string Analysis { get; set; } = "";
    public int Score { get; set; } = 10;
    public int SortOrder { get; set; }
}

public class CourseResourceFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ResourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string StoredName { get; set; } = "";
    public long Size { get; set; }
    public bool IsPinned { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Classroom
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string CourseId { get; set; } = "";
    public string TeacherUserId { get; set; } = "";
    public DateTime OpenAt { get; set; }
    public DateTime CloseAt { get; set; }
    public string Status { get; set; } = "scheduled";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ClassroomStudent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClassroomId { get; set; } = "";
    public string StudentUserId { get; set; } = "";
    public string StudentPersonId { get; set; } = "";
    public string? ClassId { get; set; }
}

public class LearningRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClassroomId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string Status { get; set; } = "notStarted";
    public int BestScore { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int? PracticeBestScore { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int? PracticeMaxScore { get; set; }
    public int AttemptCount { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? PracticeStartedAt { get; set; }
}

public class PracticeAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string LearningRecordId { get; set; } = "";
    public string AnswersJson { get; set; } = "[]";
    public int Score { get; set; }
    public int MaxScore { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}

public sealed record CourseInput(string Name, string Type, string? Description = null, string? Remark = null, string? SeriesId = null, int? Version = null);
public sealed record ChapterInput(string Name, int? Version = null);
public sealed record SectionInput(string Name, int DurationMinutes = 30, int? Version = null);
public sealed record CourseResourceInput(string Name, string Kind, int DurationMinutes = 15, string? Content = null, string? Topology = null, string? StartupNodeId = null, CourseQuestionInput[]? Questions = null, CaseRubricItem[]? CaseRubric = null, CaseTopologyBlueprint? TopologyBlueprint = null, int? Version = null);
public sealed record CourseQuestionInput(string Type, string Stem, string[]? Options, int[]? Answers, int Score = 10, string? Analysis = null);
public sealed record ClassroomInput(string Name, string CourseId, string? TeacherUserId, DateTime OpenAt, DateTime CloseAt, string[]? ClassIds = null, string[]? ProfessionIds = null, string[]? StudentUserIds = null);
public sealed record ClassroomEnrollmentInput(string[]? ClassIds = null, string[]? ProfessionIds = null, string[]? StudentUserIds = null);
public sealed record ClassroomRemovalInput(string[] UserIds);
public sealed record PracticeAnswer(string QuestionId, int[]? Selected);
public sealed record PracticeQuestionResult(string QuestionId, string Type, string Stem, string[] Options,
    int[] Selected, int[] CorrectAnswers, bool Correct, int Score, string Analysis);
public sealed record PracticeSubmissionInput(PracticeAnswer[]? Answers);
public sealed record CourseFilePinInput(bool Pinned);
public sealed record CourseFileRenameInput(string Name);
public sealed record CourseResourceImportInput(string[]? ResourceIds);

public class ClassroomGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClassroomId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ClassroomGroupMember
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; set; } = "";
    public string StudentUserId { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class ClassroomCabinetAssignment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClassroomId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string CabinetId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string AssignedByUserId { get; set; } = "";
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}

public class ClassroomCaseSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClassroomId { get; set; } = "";
    public string LearningRecordId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string? NodeId { get; set; }
    public string? VirtualMachineId { get; set; }
    public string Status { get; set; } = "active";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public DateTime? RestartedAt { get; set; }
    public int RestartCount { get; set; }
    public string WorkId { get; set; } = "";
    public int Revision { get; set; }
    public string EnvironmentJson { get; set; } = "[]";
    public string TopologyBlueprintJson { get; set; } = "null";
    public string RubricJson { get; set; } = "[]";
}

public sealed class ClassroomCaseGrade
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public bool IsFinal { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime ScoredAt { get; set; }
    public int Score { get; set; }
    public int MaxScore { get; set; }
    public int Percent { get; set; }
    public string DetailsJson { get; set; } = "[]";
}

public class CaseSessionEvidence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string StoredName { get; set; } = "";
    public string ContentType { get; set; } = "image/png";
    public long Size { get; set; }
    public string Note { get; set; } = "";
    public string CapturedByUserId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record ClassroomGroupInput(string Name, string[]? UserIds = null, string? Remark = null);
public sealed record ClassroomCabinetAssignmentInput(string[] CabinetIds, string GroupId, string ResourceId);
public sealed record ClassroomAssignmentCancelInput(string[] Ids);
