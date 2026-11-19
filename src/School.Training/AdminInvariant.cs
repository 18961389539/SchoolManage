namespace School.Training;

/// <summary>
/// Serializes operations that can reduce the administrator set and recognizes the
/// database-level guard. The database trigger remains the final authority and also
/// covers future code paths or a second process writing to the same database.
/// </summary>
public static class AdminInvariant
{
    public const string ViolationMarker = "must_keep_at_least_one_admin";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IResult> SerializeAsync(Func<Task<IResult>> action, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { Gate.Release(); }
    }

    public static bool IsViolation(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current.Message.Contains(ViolationMarker, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
