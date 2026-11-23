using Microsoft.EntityFrameworkCore;

namespace School.Training;

/// <summary>
/// Installs invariants that must hold even if a future endpoint, maintenance script,
/// or second application process bypasses the HTTP-layer checks.
/// </summary>
public static class DatabaseGuards
{
    public static async Task EnsureAsync(SchoolDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS "TR_Users_KeepAdmin_Update"
            BEFORE UPDATE OF "Role" ON "Users"
            WHEN OLD."Role" = 'Admin' AND NEW."Role" <> 'Admin'
                 AND (SELECT COUNT(*) FROM "Users" WHERE "Role" = 'Admin') <= 1
            BEGIN
                SELECT RAISE(ABORT, 'must_keep_at_least_one_admin');
            END;
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS "TR_ExamQuestions_RequireBank_Insert"
            BEFORE INSERT ON "ExamQuestions" WHEN NEW."BankId" IS NULL
            BEGIN SELECT RAISE(ABORT, 'Exam question requires a bank'); END;
            CREATE TRIGGER IF NOT EXISTS "TR_ExamQuestions_RequireBank_Update"
            BEFORE UPDATE OF "BankId" ON "ExamQuestions" WHEN NEW."BankId" IS NULL
            BEGIN SELECT RAISE(ABORT, 'Exam question requires a bank'); END;
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS "TR_Users_KeepAdmin_Delete"
            BEFORE DELETE ON "Users"
            WHEN OLD."Role" = 'Admin'
                 AND (SELECT COUNT(*) FROM "Users" WHERE "Role" = 'Admin') <= 1
            BEGIN
                SELECT RAISE(ABORT, 'must_keep_at_least_one_admin');
            END;
            """);
    }
}
