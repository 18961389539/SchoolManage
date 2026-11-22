using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace School.Training;

/// <summary>
/// Ordered, idempotent schema upgrades anchored on SQLite PRAGMA user_version.
/// A database snapshot is written before any v1 database is changed.
/// </summary>
public static class DatabaseMigrations
{
    public const int CurrentVersion = DatabaseSafety.SchemaVersion;
    const string ClassIndexName = "IX_Classes_ProfessionId_Name";

    public static async Task MigrateAsync(SchoolDbContext db, AppPaths paths, StorageOptions options, ILogger logger, CancellationToken cancellationToken = default)
    {
        var exists = File.Exists(paths.Database) && new FileInfo(paths.Database).Length > 0;
        if (!exists)
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            await SeedQuestionBankAsync(db, cancellationToken);
            await db.Database.ExecuteSqlRawAsync($"PRAGMA application_id={DatabaseSafety.ApplicationId};", cancellationToken);
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version={CurrentVersion};", cancellationToken);
            logger.LogInformation("Created database schema version {Version}.", CurrentVersion);
            return;
        }

        var version = await DatabaseSafety.Inspect(paths.Database);
        if (version > CurrentVersion) throw new InvalidDataException($"数据库结构版本 {version} 高于当前程序支持的版本 {CurrentVersion}，请升级程序后再启动");
        if (version < 1) throw new InvalidDataException("数据库缺少有效的结构版本标记，拒绝自动迁移");
        if (version == CurrentVersion) return;

        var snapshot = Path.Combine(paths.Backups, $"pre-migration-v{version}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.db");
        await DatabaseBackups.CreateSnapshotAsync(db, snapshot);
        FileRetention.Apply(paths.Backups, "pre-migration-*.db", options.PreMigrationBackupCount, long.MaxValue, protectedFile: snapshot);
        logger.LogWarning("Database schema upgrade started from version {From} to {To}; pre-upgrade snapshot: {Snapshot}", version, CurrentVersion, snapshot);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (version == 1)
            {
                var duplicates = await FindDuplicateClasses(db, cancellationToken);
                if (duplicates.Count > 0)
                    throw new InvalidOperationException("无法建立班级唯一约束，存在同专业同名班级，请先合并或重命名：" + string.Join("；", duplicates));
                await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{ClassIndexName}\";", cancellationToken);
                await db.Database.ExecuteSqlRawAsync($"CREATE UNIQUE INDEX \"{ClassIndexName}\" ON \"Classes\" (\"ProfessionId\", \"Name\");", cancellationToken);
                version = 2;
            }
            if (version == 2)
            {
                await AddColumnIfMissingAsync(db, "ImportBatches", "Status", "\"Status\" TEXT NOT NULL DEFAULT 'Preview'", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "RemoveInvalidRows", "\"RemoveInvalidRows\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "TotalCount", "\"TotalCount\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "ProcessedCount", "\"ProcessedCount\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "ImportedCount", "\"ImportedCount\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "RemovedCount", "\"RemovedCount\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "FailureMessage", "\"FailureMessage\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "QueuedAt", "\"QueuedAt\" TEXT NULL", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "StartedAt", "\"StartedAt\" TEXT NULL", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "CompletedAt", "\"CompletedAt\" TEXT NULL", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE "ImportBatches"
                    SET "Status" = CASE WHEN "Committed" = 1 THEN 'Completed' ELSE 'Preview' END;
                    """, cancellationToken);
                version = 3;
            }
            if (version == 3)
            {
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Audits_At_Id\" ON \"Audits\" (\"At\", \"Id\");", cancellationToken);
                version = 4;
            }
            if (version == 4)
            {
                var added = await AddColumnIfMissingAsync(db, "Devices", "NodeId", "\"NodeId\" TEXT NULL REFERENCES \"Nodes\" (\"Id\") ON DELETE SET NULL", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Devices_NodeId\" ON \"Devices\" (\"NodeId\");", cancellationToken);
                if (added)
                {
                    await db.Database.ExecuteSqlRawAsync("""
                        UPDATE "Devices" SET "NodeId" = 'simulation-node'
                        WHERE "Id" IN ('simulation-switch', 'simulation-router')
                          AND "CabinetId" = 'simulation-cabinet' AND "ConnectionType" = 'simulated'
                          AND EXISTS (SELECT 1 FROM "Nodes" WHERE "Id" = 'simulation-node' AND "Provider" = 'simulated');
                        """, cancellationToken);
                }
                version = 5;
            }
            if (version == 5)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "Courses" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_Courses" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "Description" TEXT NOT NULL,
                        "Type" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "Remark" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "PublishedAt" TEXT NULL
                    );
                    CREATE TABLE IF NOT EXISTS "CourseChapters" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CourseChapters" PRIMARY KEY,
                        "CourseId" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "SortOrder" INTEGER NOT NULL,
                        CONSTRAINT "FK_CourseChapters_Courses_CourseId" FOREIGN KEY ("CourseId") REFERENCES "Courses" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "CourseSections" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CourseSections" PRIMARY KEY,
                        "ChapterId" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "DurationMinutes" INTEGER NOT NULL,
                        "SortOrder" INTEGER NOT NULL,
                        CONSTRAINT "FK_CourseSections_CourseChapters_ChapterId" FOREIGN KEY ("ChapterId") REFERENCES "CourseChapters" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "CourseResources" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CourseResources" PRIMARY KEY,
                        "SectionId" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "Kind" TEXT NOT NULL,
                        "DurationMinutes" INTEGER NOT NULL,
                        "Content" TEXT NOT NULL,
                        "Topology" TEXT NOT NULL,
                        "StartupNodeId" TEXT NULL,
                        "SortOrder" INTEGER NOT NULL,
                        CONSTRAINT "FK_CourseResources_CourseSections_SectionId" FOREIGN KEY ("SectionId") REFERENCES "CourseSections" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_CourseResources_Nodes_StartupNodeId" FOREIGN KEY ("StartupNodeId") REFERENCES "Nodes" ("Id") ON DELETE SET NULL
                    );
                    CREATE TABLE IF NOT EXISTS "CourseQuestions" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CourseQuestions" PRIMARY KEY,
                        "ResourceId" TEXT NOT NULL,
                        "Type" TEXT NOT NULL,
                        "Stem" TEXT NOT NULL,
                        "OptionsJson" TEXT NOT NULL,
                        "AnswerJson" TEXT NOT NULL,
                        "Analysis" TEXT NOT NULL,
                        "Score" INTEGER NOT NULL,
                        "SortOrder" INTEGER NOT NULL,
                        CONSTRAINT "FK_CourseQuestions_CourseResources_ResourceId" FOREIGN KEY ("ResourceId") REFERENCES "CourseResources" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "Classrooms" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_Classrooms" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "CourseId" TEXT NOT NULL,
                        "TeacherUserId" TEXT NOT NULL,
                        "OpenAt" TEXT NOT NULL,
                        "CloseAt" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_Classrooms_Courses_CourseId" FOREIGN KEY ("CourseId") REFERENCES "Courses" ("Id") ON DELETE RESTRICT,
                        CONSTRAINT "FK_Classrooms_Users_TeacherUserId" FOREIGN KEY ("TeacherUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
                    );
                    CREATE TABLE IF NOT EXISTS "ClassroomStudents" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ClassroomStudents" PRIMARY KEY,
                        "ClassroomId" TEXT NOT NULL,
                        "StudentUserId" TEXT NOT NULL,
                        "StudentPersonId" TEXT NOT NULL,
                        "ClassId" TEXT NULL,
                        CONSTRAINT "FK_ClassroomStudents_Classrooms_ClassroomId" FOREIGN KEY ("ClassroomId") REFERENCES "Classrooms" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ClassroomStudents_Users_StudentUserId" FOREIGN KEY ("StudentUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "LearningRecords" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_LearningRecords" PRIMARY KEY,
                        "ClassroomId" TEXT NOT NULL,
                        "UserId" TEXT NOT NULL,
                        "ResourceId" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "BestScore" INTEGER NOT NULL,
                        "AttemptCount" INTEGER NOT NULL,
                        "StartedAt" TEXT NOT NULL,
                        "CompletedAt" TEXT NULL,
                        CONSTRAINT "FK_LearningRecords_Classrooms_ClassroomId" FOREIGN KEY ("ClassroomId") REFERENCES "Classrooms" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_LearningRecords_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_LearningRecords_CourseResources_ResourceId" FOREIGN KEY ("ResourceId") REFERENCES "CourseResources" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "PracticeAttempts" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_PracticeAttempts" PRIMARY KEY,
                        "LearningRecordId" TEXT NOT NULL,
                        "AnswersJson" TEXT NOT NULL,
                        "Score" INTEGER NOT NULL,
                        "MaxScore" INTEGER NOT NULL,
                        "SubmittedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_PracticeAttempts_LearningRecords_LearningRecordId" FOREIGN KEY ("LearningRecordId") REFERENCES "LearningRecords" ("Id") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_Courses_Name" ON "Courses" ("Name");
                    CREATE INDEX IF NOT EXISTS "IX_Courses_Status_Type" ON "Courses" ("Status", "Type");
                    CREATE INDEX IF NOT EXISTS "IX_CourseChapters_CourseId_SortOrder" ON "CourseChapters" ("CourseId", "SortOrder");
                    CREATE INDEX IF NOT EXISTS "IX_CourseSections_ChapterId_SortOrder" ON "CourseSections" ("ChapterId", "SortOrder");
                    CREATE INDEX IF NOT EXISTS "IX_CourseResources_SectionId_SortOrder" ON "CourseResources" ("SectionId", "SortOrder");
                    CREATE INDEX IF NOT EXISTS "IX_CourseResources_StartupNodeId" ON "CourseResources" ("StartupNodeId");
                    CREATE INDEX IF NOT EXISTS "IX_CourseQuestions_ResourceId_SortOrder" ON "CourseQuestions" ("ResourceId", "SortOrder");
                    CREATE INDEX IF NOT EXISTS "IX_Classrooms_TeacherUserId" ON "Classrooms" ("TeacherUserId");
                    CREATE INDEX IF NOT EXISTS "IX_Classrooms_CourseId" ON "Classrooms" ("CourseId");
                    CREATE INDEX IF NOT EXISTS "IX_Classrooms_Status" ON "Classrooms" ("Status");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClassroomStudents_ClassroomId_StudentUserId" ON "ClassroomStudents" ("ClassroomId", "StudentUserId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomStudents_StudentUserId" ON "ClassroomStudents" ("StudentUserId");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_LearningRecords_ClassroomId_UserId_ResourceId" ON "LearningRecords" ("ClassroomId", "UserId", "ResourceId");
                    CREATE INDEX IF NOT EXISTS "IX_LearningRecords_UserId" ON "LearningRecords" ("UserId");
                    CREATE INDEX IF NOT EXISTS "IX_LearningRecords_ResourceId" ON "LearningRecords" ("ResourceId");
                    CREATE INDEX IF NOT EXISTS "IX_PracticeAttempts_LearningRecordId_SubmittedAt" ON "PracticeAttempts" ("LearningRecordId", "SubmittedAt");
                    """, cancellationToken);
                version = 6;
            }
            if (version == 6)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "ExamQuestions" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ExamQuestions" PRIMARY KEY,
                        "Type" TEXT NOT NULL,
                        "Stem" TEXT NOT NULL,
                        "OptionsJson" TEXT NOT NULL,
                        "AnswerJson" TEXT NOT NULL,
                        "Analysis" TEXT NOT NULL,
                        "Score" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS "ExamPapers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ExamPapers" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "Description" TEXT NOT NULL,
                        "Remark" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS "ExamPaperQuestions" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ExamPaperQuestions" PRIMARY KEY,
                        "PaperId" TEXT NOT NULL,
                        "QuestionId" TEXT NOT NULL,
                        "Type" TEXT NOT NULL,
                        "Stem" TEXT NOT NULL,
                        "OptionsJson" TEXT NOT NULL,
                        "AnswerJson" TEXT NOT NULL,
                        "Analysis" TEXT NOT NULL,
                        "Score" INTEGER NOT NULL,
                        "SortOrder" INTEGER NOT NULL,
                        CONSTRAINT "FK_ExamPaperQuestions_ExamPapers_PaperId" FOREIGN KEY ("PaperId") REFERENCES "ExamPapers" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ExamPaperQuestions_ExamQuestions_QuestionId" FOREIGN KEY ("QuestionId") REFERENCES "ExamQuestions" ("Id") ON DELETE RESTRICT
                    );
                    CREATE TABLE IF NOT EXISTS "TrainingExams" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_TrainingExams" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "PaperId" TEXT NOT NULL,
                        "Description" TEXT NOT NULL,
                        "StartAt" TEXT NOT NULL,
                        "EndAt" TEXT NOT NULL,
                        "DurationMinutes" INTEGER NOT NULL,
                        "Status" TEXT NOT NULL,
                        "CreatedByUserId" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_TrainingExams_ExamPapers_PaperId" FOREIGN KEY ("PaperId") REFERENCES "ExamPapers" ("Id") ON DELETE RESTRICT,
                        CONSTRAINT "FK_TrainingExams_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
                    );
                    CREATE TABLE IF NOT EXISTS "ExamStudents" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ExamStudents" PRIMARY KEY,
                        "ExamId" TEXT NOT NULL,
                        "StudentUserId" TEXT NOT NULL,
                        "StudentPersonId" TEXT NOT NULL,
                        "ClassId" TEXT NULL,
                        "ClassName" TEXT NOT NULL,
                        "DeploymentStatus" TEXT NOT NULL,
                        "DeploymentMessage" TEXT NOT NULL,
                        "DeployedAt" TEXT NULL,
                        "AddedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_ExamStudents_TrainingExams_ExamId" FOREIGN KEY ("ExamId") REFERENCES "TrainingExams" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ExamStudents_Users_StudentUserId" FOREIGN KEY ("StudentUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ExamStudents_Classes_ClassId" FOREIGN KEY ("ClassId") REFERENCES "Classes" ("Id") ON DELETE SET NULL
                    );
                    CREATE TABLE IF NOT EXISTS "ExamAttempts" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ExamAttempts" PRIMARY KEY,
                        "ExamId" TEXT NOT NULL,
                        "UserId" TEXT NOT NULL,
                        "StudentName" TEXT NOT NULL,
                        "StudentUserName" TEXT NOT NULL,
                        "StudentNumber" TEXT NOT NULL,
                        "ClassName" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "StartedAt" TEXT NOT NULL,
                        "Deadline" TEXT NOT NULL,
                        "SubmittedAt" TEXT NULL,
                        "AutoSubmitted" INTEGER NOT NULL,
                        "ObjectiveScore" INTEGER NOT NULL,
                        "PracticalScore" INTEGER NOT NULL,
                        "TotalScore" INTEGER NOT NULL,
                        "MaxScore" INTEGER NOT NULL,
                        "GradedAt" TEXT NULL,
                        "GradedByUserId" TEXT NOT NULL,
                        CONSTRAINT "FK_ExamAttempts_TrainingExams_ExamId" FOREIGN KEY ("ExamId") REFERENCES "TrainingExams" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ExamAttempts_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "ExamAnswers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ExamAnswers" PRIMARY KEY,
                        "AttemptId" TEXT NOT NULL,
                        "PaperQuestionId" TEXT NOT NULL,
                        "SelectedJson" TEXT NOT NULL,
                        "TextAnswer" TEXT NOT NULL,
                        "ScoreAwarded" INTEGER NULL,
                        "Comment" TEXT NOT NULL,
                        "AnsweredAt" TEXT NULL,
                        CONSTRAINT "FK_ExamAnswers_ExamAttempts_AttemptId" FOREIGN KEY ("AttemptId") REFERENCES "ExamAttempts" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ExamAnswers_ExamPaperQuestions_PaperQuestionId" FOREIGN KEY ("PaperQuestionId") REFERENCES "ExamPaperQuestions" ("Id") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamPapers_Name" ON "ExamPapers" ("Name");
                    CREATE INDEX IF NOT EXISTS "IX_ExamQuestions_Type" ON "ExamQuestions" ("Type");
                    CREATE INDEX IF NOT EXISTS "IX_ExamQuestions_CreatedAt" ON "ExamQuestions" ("CreatedAt");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamPaperQuestions_PaperId_QuestionId" ON "ExamPaperQuestions" ("PaperId", "QuestionId");
                    CREATE INDEX IF NOT EXISTS "IX_ExamPaperQuestions_PaperId_SortOrder" ON "ExamPaperQuestions" ("PaperId", "SortOrder");
                    CREATE INDEX IF NOT EXISTS "IX_ExamPaperQuestions_QuestionId" ON "ExamPaperQuestions" ("QuestionId");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_Exams_Name" ON "TrainingExams" ("Name");
                    CREATE INDEX IF NOT EXISTS "IX_Exams_Status_StartAt" ON "TrainingExams" ("Status", "StartAt");
                    CREATE INDEX IF NOT EXISTS "IX_Exams_PaperId" ON "TrainingExams" ("PaperId");
                    CREATE INDEX IF NOT EXISTS "IX_Exams_CreatedByUserId" ON "TrainingExams" ("CreatedByUserId");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamStudents_ExamId_StudentUserId" ON "ExamStudents" ("ExamId", "StudentUserId");
                    CREATE INDEX IF NOT EXISTS "IX_ExamStudents_StudentUserId" ON "ExamStudents" ("StudentUserId");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamAttempts_ExamId_UserId" ON "ExamAttempts" ("ExamId", "UserId");
                    CREATE INDEX IF NOT EXISTS "IX_ExamAttempts_UserId" ON "ExamAttempts" ("UserId");
                    CREATE INDEX IF NOT EXISTS "IX_ExamAttempts_Status" ON "ExamAttempts" ("Status");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamAnswers_AttemptId_PaperQuestionId" ON "ExamAnswers" ("AttemptId", "PaperQuestionId");
                    CREATE INDEX IF NOT EXISTS "IX_ExamAnswers_PaperQuestionId" ON "ExamAnswers" ("PaperQuestionId");
                    """, cancellationToken);
                version = 7;
            }
            if (version == 7)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "CourseResourceFiles" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CourseResourceFiles" PRIMARY KEY,
                        "ResourceId" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "StoredName" TEXT NOT NULL,
                        "Size" INTEGER NOT NULL,
                        "IsPinned" INTEGER NOT NULL,
                        "SortOrder" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_CourseResourceFiles_CourseResources_ResourceId" FOREIGN KEY ("ResourceId") REFERENCES "CourseResources" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "JobPositions" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_JobPositions" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "Description" TEXT NOT NULL,
                        "DurationMinutes" INTEGER NOT NULL,
                        "StartupNodeId" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_JobPositions_Nodes_StartupNodeId" FOREIGN KEY ("StartupNodeId") REFERENCES "Nodes" ("Id") ON DELETE SET NULL
                    );
                    CREATE TABLE IF NOT EXISTS "JobSkills" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_JobSkills" PRIMARY KEY,
                        "PositionId" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "SubName" TEXT NOT NULL,
                        "Ability" TEXT NOT NULL,
                        "SortOrder" INTEGER NOT NULL,
                        CONSTRAINT "FK_JobSkills_JobPositions_PositionId" FOREIGN KEY ("PositionId") REFERENCES "JobPositions" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "JobQuestions" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_JobQuestions" PRIMARY KEY,
                        "PositionId" TEXT NOT NULL,
                        "SkillId" TEXT NULL,
                        "Type" TEXT NOT NULL,
                        "Stem" TEXT NOT NULL,
                        "OptionsJson" TEXT NOT NULL,
                        "AnswerJson" TEXT NOT NULL,
                        "Analysis" TEXT NOT NULL,
                        "RubricJson" TEXT NOT NULL,
                        "Score" INTEGER NOT NULL,
                        "SortOrder" INTEGER NOT NULL,
                        CONSTRAINT "FK_JobQuestions_JobPositions_PositionId" FOREIGN KEY ("PositionId") REFERENCES "JobPositions" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_JobQuestions_JobSkills_SkillId" FOREIGN KEY ("SkillId") REFERENCES "JobSkills" ("Id") ON DELETE SET NULL
                    );
                    CREATE TABLE IF NOT EXISTS "JobAttempts" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_JobAttempts" PRIMARY KEY,
                        "PositionId" TEXT NOT NULL,
                        "UserId" TEXT NOT NULL,
                        "StudentName" TEXT NOT NULL,
                        "StudentUserName" TEXT NOT NULL,
                        "StudentNumber" TEXT NOT NULL,
                        "ClassName" TEXT NOT NULL,
                        "VirtualMachineId" TEXT NULL,
                        "Status" TEXT NOT NULL,
                        "StartedAt" TEXT NOT NULL,
                        "Deadline" TEXT NOT NULL,
                        "SubmittedAt" TEXT NULL,
                        "AutoSubmitted" INTEGER NOT NULL,
                        "ObjectiveScore" INTEGER NOT NULL,
                        "PracticalScore" INTEGER NOT NULL,
                        "TotalScore" INTEGER NOT NULL,
                        "MaxScore" INTEGER NOT NULL,
                        "GradedAt" TEXT NULL,
                        CONSTRAINT "FK_JobAttempts_JobPositions_PositionId" FOREIGN KEY ("PositionId") REFERENCES "JobPositions" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_JobAttempts_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_JobAttempts_VirtualMachines_VirtualMachineId" FOREIGN KEY ("VirtualMachineId") REFERENCES "VirtualMachines" ("Id") ON DELETE SET NULL
                    );
                    CREATE TABLE IF NOT EXISTS "JobAnswers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_JobAnswers" PRIMARY KEY,
                        "AttemptId" TEXT NOT NULL,
                        "QuestionId" TEXT NOT NULL,
                        "SelectedJson" TEXT NOT NULL,
                        "TextAnswer" TEXT NOT NULL,
                        "ScoreAwarded" INTEGER NOT NULL,
                        "AnsweredAt" TEXT NULL,
                        CONSTRAINT "FK_JobAnswers_JobAttempts_AttemptId" FOREIGN KEY ("AttemptId") REFERENCES "JobAttempts" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_JobAnswers_JobQuestions_QuestionId" FOREIGN KEY ("QuestionId") REFERENCES "JobQuestions" ("Id") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_CourseResourceFiles_ResourceId_SortOrder" ON "CourseResourceFiles" ("ResourceId", "SortOrder");
                    CREATE INDEX IF NOT EXISTS "IX_CourseResourceFiles_ResourceId_IsPinned" ON "CourseResourceFiles" ("ResourceId", "IsPinned");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_JobPositions_Name" ON "JobPositions" ("Name");
                    CREATE INDEX IF NOT EXISTS "IX_JobPositions_StartupNodeId" ON "JobPositions" ("StartupNodeId");
                    CREATE INDEX IF NOT EXISTS "IX_JobSkills_PositionId_SortOrder" ON "JobSkills" ("PositionId", "SortOrder");
                    CREATE INDEX IF NOT EXISTS "IX_JobQuestions_PositionId_SortOrder" ON "JobQuestions" ("PositionId", "SortOrder");
                    CREATE INDEX IF NOT EXISTS "IX_JobQuestions_SkillId" ON "JobQuestions" ("SkillId");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_JobAttempts_PositionId_UserId" ON "JobAttempts" ("PositionId", "UserId");
                    CREATE INDEX IF NOT EXISTS "IX_JobAttempts_UserId" ON "JobAttempts" ("UserId");
                    CREATE INDEX IF NOT EXISTS "IX_JobAttempts_Status" ON "JobAttempts" ("Status");
                    CREATE INDEX IF NOT EXISTS "IX_JobAttempts_VirtualMachineId" ON "JobAttempts" ("VirtualMachineId");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_JobAnswers_AttemptId_QuestionId" ON "JobAnswers" ("AttemptId", "QuestionId");
                    CREATE INDEX IF NOT EXISTS "IX_JobAnswers_QuestionId" ON "JobAnswers" ("QuestionId");
                    """, cancellationToken);
                version = 8;
            }
            if (version == 8)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "ClassroomGroups" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ClassroomGroups" PRIMARY KEY,
                        "ClassroomId" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "Remark" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_ClassroomGroups_Classrooms_ClassroomId" FOREIGN KEY ("ClassroomId") REFERENCES "Classrooms" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "ClassroomGroupMembers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ClassroomGroupMembers" PRIMARY KEY,
                        "GroupId" TEXT NOT NULL,
                        "StudentUserId" TEXT NOT NULL,
                        "AddedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_ClassroomGroupMembers_ClassroomGroups_GroupId" FOREIGN KEY ("GroupId") REFERENCES "ClassroomGroups" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ClassroomGroupMembers_Users_StudentUserId" FOREIGN KEY ("StudentUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "ClassroomCabinetAssignments" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ClassroomCabinetAssignments" PRIMARY KEY,
                        "ClassroomId" TEXT NOT NULL,
                        "GroupId" TEXT NOT NULL,
                        "CabinetId" TEXT NOT NULL,
                        "ResourceId" TEXT NOT NULL,
                        "AssignedByUserId" TEXT NOT NULL,
                        "AssignedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_ClassroomCabinetAssignments_Classrooms_ClassroomId" FOREIGN KEY ("ClassroomId") REFERENCES "Classrooms" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ClassroomCabinetAssignments_ClassroomGroups_GroupId" FOREIGN KEY ("GroupId") REFERENCES "ClassroomGroups" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ClassroomCabinetAssignments_Cabinets_CabinetId" FOREIGN KEY ("CabinetId") REFERENCES "Cabinets" ("Id") ON DELETE RESTRICT,
                        CONSTRAINT "FK_ClassroomCabinetAssignments_CourseResources_ResourceId" FOREIGN KEY ("ResourceId") REFERENCES "CourseResources" ("Id") ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS "ClassroomCaseSessions" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ClassroomCaseSessions" PRIMARY KEY,
                        "ClassroomId" TEXT NOT NULL,
                        "LearningRecordId" TEXT NOT NULL,
                        "UserId" TEXT NOT NULL,
                        "ResourceId" TEXT NOT NULL,
                        "NodeId" TEXT NULL,
                        "VirtualMachineId" TEXT NULL,
                        "Status" TEXT NOT NULL,
                        "StartedAt" TEXT NOT NULL,
                        "EndedAt" TEXT NULL,
                        "RestartedAt" TEXT NULL,
                        "RestartCount" INTEGER NOT NULL,
                        CONSTRAINT "FK_ClassroomCaseSessions_Classrooms_ClassroomId" FOREIGN KEY ("ClassroomId") REFERENCES "Classrooms" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ClassroomCaseSessions_LearningRecords_LearningRecordId" FOREIGN KEY ("LearningRecordId") REFERENCES "LearningRecords" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ClassroomCaseSessions_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ClassroomCaseSessions_CourseResources_ResourceId" FOREIGN KEY ("ResourceId") REFERENCES "CourseResources" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ClassroomCaseSessions_Nodes_NodeId" FOREIGN KEY ("NodeId") REFERENCES "Nodes" ("Id") ON DELETE SET NULL,
                        CONSTRAINT "FK_ClassroomCaseSessions_VirtualMachines_VirtualMachineId" FOREIGN KEY ("VirtualMachineId") REFERENCES "VirtualMachines" ("Id") ON DELETE SET NULL
                    );
                    CREATE TABLE IF NOT EXISTS "CaseSessionEvidences" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CaseSessionEvidences" PRIMARY KEY,
                        "SessionId" TEXT NOT NULL,
                        "FileName" TEXT NOT NULL,
                        "StoredName" TEXT NOT NULL,
                        "ContentType" TEXT NOT NULL,
                        "Size" INTEGER NOT NULL,
                        "Note" TEXT NOT NULL,
                        "CapturedByUserId" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_CaseSessionEvidences_ClassroomCaseSessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "ClassroomCaseSessions" ("Id") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClassroomGroups_ClassroomId_Name" ON "ClassroomGroups" ("ClassroomId", "Name");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClassroomGroupMembers_GroupId_StudentUserId" ON "ClassroomGroupMembers" ("GroupId", "StudentUserId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomGroupMembers_StudentUserId" ON "ClassroomGroupMembers" ("StudentUserId");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClassroomCabinetAssignments_CabinetId" ON "ClassroomCabinetAssignments" ("CabinetId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCabinetAssignments_ClassroomId" ON "ClassroomCabinetAssignments" ("ClassroomId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCabinetAssignments_GroupId" ON "ClassroomCabinetAssignments" ("GroupId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCabinetAssignments_ResourceId" ON "ClassroomCabinetAssignments" ("ResourceId");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClassroomCaseSessions_LearningRecordId" ON "ClassroomCaseSessions" ("LearningRecordId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCaseSessions_ClassroomId_Status" ON "ClassroomCaseSessions" ("ClassroomId", "Status");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCaseSessions_UserId" ON "ClassroomCaseSessions" ("UserId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCaseSessions_ResourceId" ON "ClassroomCaseSessions" ("ResourceId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCaseSessions_NodeId" ON "ClassroomCaseSessions" ("NodeId");
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCaseSessions_VirtualMachineId" ON "ClassroomCaseSessions" ("VirtualMachineId");
                    CREATE INDEX IF NOT EXISTS "IX_CaseSessionEvidences_SessionId_CreatedAt" ON "CaseSessionEvidences" ("SessionId", "CreatedAt");
                    """, cancellationToken);
                version = 9;
            }
            if (version == 9)
            {
                await AddColumnIfMissingAsync(db, "CourseResources", "CaseRubricJson", "\"CaseRubricJson\" TEXT NOT NULL DEFAULT '[]'", cancellationToken);
                await AddColumnIfMissingAsync(db, "ClassroomCaseSessions", "WorkId", "\"WorkId\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                await AddColumnIfMissingAsync(db, "ClassroomCaseSessions", "Revision", "\"Revision\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ClassroomCaseSessions", "EnvironmentJson", "\"EnvironmentJson\" TEXT NOT NULL DEFAULT '[]'", cancellationToken);
                await AddColumnIfMissingAsync(db, "ClassroomCaseSessions", "RubricJson", "\"RubricJson\" TEXT NOT NULL DEFAULT '[]'", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "ClassroomCaseGrades" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ClassroomCaseGrades" PRIMARY KEY,
                        "SessionId" TEXT NOT NULL,
                        "IsFinal" INTEGER NOT NULL,
                        "StartedAt" TEXT NOT NULL,
                        "ScoredAt" TEXT NOT NULL,
                        "Score" INTEGER NOT NULL,
                        "MaxScore" INTEGER NOT NULL,
                        "Percent" INTEGER NOT NULL,
                        "DetailsJson" TEXT NOT NULL,
                        CONSTRAINT "FK_ClassroomCaseGrades_ClassroomCaseSessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "ClassroomCaseSessions" ("Id") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ClassroomCaseGrades_SessionId_ScoredAt" ON "ClassroomCaseGrades" ("SessionId", "ScoredAt");
                    """, cancellationToken);
                version = 10;
            }
            if (version == 10)
            {
                foreach (var table in new[] { "ExamQuestions", "ExamPaperQuestions" })
                {
                    await AddColumnIfMissingAsync(db, table, "PracticalNodeId", "\"PracticalNodeId\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                    await AddColumnIfMissingAsync(db, table, "PracticalRubricJson", "\"PracticalRubricJson\" TEXT NOT NULL DEFAULT '[]'", cancellationToken);
                }
                await AddColumnIfMissingAsync(db, "ExamAnswers", "PracticalDetailsJson", "\"PracticalDetailsJson\" TEXT NOT NULL DEFAULT '[]'", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "ExamPracticalEnvironments" (
                        "EnrollmentId" TEXT NOT NULL,
                        "PaperQuestionId" TEXT NOT NULL,
                        "BaselineJson" TEXT NOT NULL,
                        "ConfigurationJson" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL,
                        "HasWork" INTEGER NOT NULL,
                        "DeployedAt" TEXT NOT NULL,
                        "RestartedAt" TEXT NULL,
                        CONSTRAINT "PK_ExamPracticalEnvironments" PRIMARY KEY ("EnrollmentId", "PaperQuestionId"),
                        CONSTRAINT "FK_ExamPracticalEnvironments_ExamStudents_EnrollmentId" FOREIGN KEY ("EnrollmentId") REFERENCES "ExamStudents" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_ExamPracticalEnvironments_ExamPaperQuestions_PaperQuestionId" FOREIGN KEY ("PaperQuestionId") REFERENCES "ExamPaperQuestions" ("Id") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ExamPracticalEnvironments_PaperQuestionId" ON "ExamPracticalEnvironments" ("PaperQuestionId");
                    """, cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE "ExamStudents" SET "DeploymentStatus" = 'pending', "DeploymentMessage" = '', "DeployedAt" = NULL
                    WHERE "DeploymentStatus" = 'deployed' AND NOT EXISTS (
                        SELECT 1 FROM "ExamPracticalEnvironments" WHERE "EnrollmentId" = "ExamStudents"."Id"
                    );
                    """, cancellationToken);
                version = 11;
            }
            if (version == 11)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "ExamQuestionBanks" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ExamQuestionBanks" PRIMARY KEY,
                        "Name" TEXT COLLATE NOCASE NOT NULL,
                        "Type" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamQuestionBanks_Name" ON "ExamQuestionBanks" ("Name");
                    """, cancellationToken);
                await SeedQuestionBankAsync(db, cancellationToken);
                // SQLite can append a nullable FK without rebuilding the table referenced by existing papers.
                await AddColumnIfMissingAsync(db, "ExamQuestions", "BankId",
                    "\"BankId\" TEXT NULL REFERENCES \"ExamQuestionBanks\" (\"Id\") ON DELETE RESTRICT", cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"ExamQuestions\" SET \"BankId\" = {ExamQuestionBank.DefaultId} WHERE \"BankId\" IS NULL;", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE INDEX IF NOT EXISTS "IX_ExamQuestions_BankId_Type" ON "ExamQuestions" ("BankId", "Type");
                    CREATE TRIGGER IF NOT EXISTS "TR_ExamQuestions_RequireBank_Insert"
                    BEFORE INSERT ON "ExamQuestions" WHEN NEW."BankId" IS NULL
                    BEGIN SELECT RAISE(ABORT, 'Exam question requires a bank'); END;
                    CREATE TRIGGER IF NOT EXISTS "TR_ExamQuestions_RequireBank_Update"
                    BEFORE UPDATE OF "BankId" ON "ExamQuestions" WHEN NEW."BankId" IS NULL
                    BEGIN SELECT RAISE(ABORT, 'Exam question requires a bank'); END;
                    """, cancellationToken);
                version = 12;
            }
            if (version == 12)
            {
                await AddColumnIfMissingAsync(db, "VirtualMachines", "Uuid", "\"Uuid\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                var addIp = await AddColumnIfMissingAsync(db, "VirtualMachines", "IpAddress", "\"IpAddress\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                var unnumbered = await db.VirtualMachines.Where(vm => vm.Uuid == "").ToListAsync(cancellationToken);
                foreach (var vm in unnumbered)
                    vm.Uuid = Guid.TryParse(vm.Id, out var legacyId) ? legacyId.ToString() : Guid.NewGuid().ToString();
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                if (addIp)
                    await db.Database.ExecuteSqlRawAsync("""
                        WITH addresses AS (
                            SELECT "Id", row_number() OVER (ORDER BY "Id") - 1 AS n FROM "VirtualMachines"
                        )
                        UPDATE "VirtualMachines" SET "IpAddress" = (
                            SELECT printf('10.%d.%d.%d', n / 65024, (n / 254) % 256, n % 254 + 1)
                            FROM addresses WHERE addresses."Id" = "VirtualMachines"."Id"
                        );
                        """, cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_VirtualMachines_Uuid" ON "VirtualMachines" ("Uuid");
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_VirtualMachines_IpAddress" ON "VirtualMachines" ("IpAddress");
                    """, cancellationToken);
                version = 13;
            }
            if (version == 13)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "CoreCourseSeries" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CoreCourseSeries" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "Description" TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_CoreCourseSeries_Name" ON "CoreCourseSeries" ("Name");
                    """, cancellationToken);
                if (await AddColumnIfMissingAsync(db, "Courses", "IsActive", "\"IsActive\" INTEGER NOT NULL DEFAULT 0", cancellationToken))
                    await db.Database.ExecuteSqlRawAsync("""UPDATE "Courses" SET "IsActive" = 1 WHERE "Status" = 'published';""", cancellationToken);
                await AddColumnIfMissingAsync(db, "Courses", "SeriesId",
                    "\"SeriesId\" TEXT NULL REFERENCES \"CoreCourseSeries\" (\"Id\") ON DELETE SET NULL", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_Courses_SeriesId" ON "Courses" ("SeriesId");""", cancellationToken);
                await AddColumnIfMissingAsync(db, "LearningRecords", "PracticeStartedAt", "\"PracticeStartedAt\" TEXT NULL", cancellationToken);
                await AddColumnIfMissingAsync(db, "PracticeAttempts", "StartedAt", "\"StartedAt\" TEXT NULL", cancellationToken);
                await AddColumnIfMissingAsync(db, "JobQuestions", "PracticalRubricJson", "\"PracticalRubricJson\" TEXT NOT NULL DEFAULT '[]'", cancellationToken);
                await AddColumnIfMissingAsync(db, "JobAttempts", "EnvironmentJson", "\"EnvironmentJson\" TEXT NOT NULL DEFAULT '[]'", cancellationToken);
                await AddColumnIfMissingAsync(db, "JobAnswers", "PracticalDetailsJson", "\"PracticalDetailsJson\" TEXT NOT NULL DEFAULT '[]'", cancellationToken);
                version = 14;
            }
            if (version == 14)
            {
                await AddColumnIfMissingAsync(db, "CourseResources", "TopologyBlueprintJson", "\"TopologyBlueprintJson\" TEXT NOT NULL DEFAULT 'null'", cancellationToken);
                await AddColumnIfMissingAsync(db, "ClassroomCaseSessions", "TopologyBlueprintJson", "\"TopologyBlueprintJson\" TEXT NOT NULL DEFAULT 'null'", cancellationToken);
                await AddColumnIfMissingAsync(db, "ExamPracticalEnvironments", "VmUuid", "\"VmUuid\" TEXT NULL", cancellationToken);
                await AddColumnIfMissingAsync(db, "ExamPracticalEnvironments", "DeploymentStatus", "\"DeploymentStatus\" TEXT NOT NULL DEFAULT 'deployed'", cancellationToken);
                await AddColumnIfMissingAsync(db, "ExamPracticalEnvironments", "DeploymentMessage", "\"DeploymentMessage\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE "ExamPracticalEnvironments"
                    SET "VmUuid" = lower(hex(randomblob(16))), "DeploymentMessage" = '模拟环境已就绪'
                    WHERE "VmUuid" IS NULL AND "DeploymentStatus" = 'deployed';
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamPracticalEnvironments_VmUuid" ON "ExamPracticalEnvironments" ("VmUuid");
                    CREATE TABLE IF NOT EXISTS "CaseTopologyTemplates" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CaseTopologyTemplates" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "NodeId" TEXT NOT NULL,
                        "BlueprintJson" TEXT NOT NULL,
                        CONSTRAINT "FK_CaseTopologyTemplates_Nodes_NodeId" FOREIGN KEY ("NodeId") REFERENCES "Nodes" ("Id") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_CaseTopologyTemplates_NodeId_Name" ON "CaseTopologyTemplates" ("NodeId","Name");
                    """, cancellationToken);
                version = 15;
            }
            if (version == 15)
            {
                await AddColumnIfMissingAsync(db, "ExamQuestions", "PracticalResources", "\"PracticalResources\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                await AddColumnIfMissingAsync(db, "ExamPaperQuestions", "PracticalResources", "\"PracticalResources\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                version = 16;
            }
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version={CurrentVersion};", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        logger.LogInformation("Database schema upgraded to version {Version}.", version);
    }

    static Task SeedQuestionBankAsync(SchoolDbContext db, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT OR IGNORE INTO "ExamQuestionBanks" ("Id", "Name", "Type", "CreatedAt")
            VALUES ({ExamQuestionBank.DefaultId}, {"默认题库"}, {"mixed"}, {DateTime.UtcNow});
            """, ct);

    static async Task<bool> AddColumnIfMissingAsync(SchoolDbContext db, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var info = connection.CreateCommand();
        info.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        info.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        await using var alter = connection.CreateCommand();
        alter.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    static async Task<List<string>> FindDuplicateClasses(SchoolDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT "ProfessionId", "Name", COUNT(*) AS "Count"
            FROM "Classes"
            GROUP BY "ProfessionId", "Name"
            HAVING COUNT(*) > 1
            ORDER BY "Count" DESC
            LIMIT 20;
            """;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add($"{reader.GetString(1)}（专业 {reader.GetString(0)}，{reader.GetInt64(2)} 条）");
        return result;
    }
}
