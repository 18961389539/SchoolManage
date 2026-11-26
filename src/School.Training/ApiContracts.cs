namespace School.Training;

/// <summary>
/// Named JSON responses for the OpenAPI document "contract".
/// Session, status, and exam-question reads are the first slice; other routes still
/// return anonymous objects and stay in the hand-written web/src/types.ts until they move here.
/// Property names serialize as camelCase through the shared HTTP JSON options.
/// </summary>
public sealed record MessageResponse(string Message);

public sealed record SessionUserResponse(string Id, string UserName, string DisplayName, string Role, bool MustChangePassword);

public sealed record SessionResponse(SessionUserResponse User, string CsrfToken);

public sealed record StatusDemoAccount(string Role, string UserName, string DisplayName);

public sealed record StatusResponse(
    bool Initialized,
    string Product,
    string Version,
    int SchemaVersion,
    long LogWriteFailures,
    int MaxUploadBytes,
    string? SchoolName,
    bool Demo,
    string? DemoPassword,
    StatusDemoAccount[]? DemoAccounts);

public sealed record ExamQuestionSummaryResponse(
    string Id,
    string BankId,
    string BankName,
    string Type,
    string Stem,
    string[] Options,
    int[] Answers,
    string Analysis,
    int Score,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool AutomaticPractical,
    int PaperCount);

public sealed record ExamQuestionPaperRef(string Id, string Name);

public sealed record ExamQuestionDetailResponse(
    string Id,
    string BankId,
    string BankName,
    string Type,
    string Stem,
    string[] Options,
    int[] Answers,
    string Analysis,
    int Score,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool AutomaticPractical,
    string PracticalNodeId,
    string PracticalResources,
    CaseRubricItem[] PracticalRubric,
    ExamQuestionPaperRef[] Papers);

public sealed record ExamQuestionListResponse(int Total, int Page, int PageSize, ExamQuestionSummaryResponse[] Items);
