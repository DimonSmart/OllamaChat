namespace ChatClient.Application.Services.TaskSessions;

public sealed record TaskSessionContextInfo(string DatabaseFile);

public sealed record TaskSessionSnapshot(
    string SessionId,
    string? Title,
    string? Description,
    string? Phase,
    string Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int TurnCount,
    IReadOnlyList<TaskSessionDocumentInfo> Documents,
    IReadOnlyList<TaskSessionParameterInfo> Parameters,
    IReadOnlyList<TaskSessionSummaryInfo> Summaries);

public sealed record TaskSessionDocumentInfo(
    string Kind,
    string? Title,
    string? Source,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TaskSessionDocumentSnapshot(
    string SessionId,
    string Kind,
    string? Title,
    string Markdown,
    string? Source,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TaskSessionParameterInfo(
    string Key,
    string ValueKind,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TaskSessionParameterSnapshot(
    string SessionId,
    string Key,
    string ValueKind,
    string Value,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TaskSessionTurnSnapshot(
    string SessionId,
    long Sequence,
    string Role,
    string? SpeakerId,
    string Content,
    DateTime CreatedAtUtc);

public sealed record TaskSessionSummaryInfo(
    string Label,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TaskSessionSummarySnapshot(
    string SessionId,
    string Label,
    string Markdown,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
