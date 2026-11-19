using Microsoft.Data.Sqlite;

namespace School.Training;

public enum DatabaseFailure { Constraint, Busy, Other }

public static class DatabaseErrors
{
    public static DatabaseFailure Classify(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is not SqliteException sqlite) continue;
            var primary = (sqlite.SqliteExtendedErrorCode != 0 ? sqlite.SqliteExtendedErrorCode : sqlite.SqliteErrorCode) & 0xff;
            if (primary is 5 or 6) return DatabaseFailure.Busy;
            if (primary == 19) return DatabaseFailure.Constraint;
        }
        return DatabaseFailure.Other;
    }
}
