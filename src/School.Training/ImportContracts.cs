namespace School.Training;

/// <summary>
/// Response shapes of the import API. They live in records so the contract is a named type that
/// tests can pin, not an anonymous object that can silently rename a field.
/// </summary>
public sealed record ImportPreviewResponse(string BatchId, List<ImportRow> Rows, int ValidCount, int ErrorCount);

public sealed record ImportStatusResponse(
    string BatchId,
    string Status,
    int ProcessedCount,
    int TotalCount,
    int ImportedCount,
    int RemovedCount,
    string FailureMessage,
    DateTime CreatedAt,
    DateTime? QueuedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string Message);
