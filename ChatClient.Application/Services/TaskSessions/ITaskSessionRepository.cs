namespace ChatClient.Application.Services.TaskSessions;

public interface ITaskSessionRepository
{
    Task<TaskSessionSnapshot> CreateSessionAsync(
        string databaseFilePath,
        string sessionId,
        string? title,
        string? description,
        CancellationToken cancellationToken = default);

    Task<TaskSessionSnapshot> GetSessionAsync(
        string databaseFilePath,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<TaskSessionSnapshot> SetPhaseAsync(
        string databaseFilePath,
        string sessionId,
        string phase,
        CancellationToken cancellationToken = default);

    Task<TaskSessionDocumentSnapshot> UpsertDocumentAsync(
        string databaseFilePath,
        string sessionId,
        string kind,
        string markdown,
        string? title,
        string? source,
        CancellationToken cancellationToken = default);

    Task<TaskSessionDocumentSnapshot> GetDocumentAsync(
        string databaseFilePath,
        string sessionId,
        string kind,
        CancellationToken cancellationToken = default);

    Task<TaskSessionParameterSnapshot> UpsertParameterAsync(
        string databaseFilePath,
        string sessionId,
        string key,
        string valueKind,
        string value,
        CancellationToken cancellationToken = default);

    Task<TaskSessionParameterSnapshot> GetParameterAsync(
        string databaseFilePath,
        string sessionId,
        string key,
        CancellationToken cancellationToken = default);

    Task<TaskSessionTurnSnapshot> AppendTurnAsync(
        string databaseFilePath,
        string sessionId,
        string role,
        string content,
        string? speakerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskSessionTurnSnapshot>> ListTurnsAsync(
        string databaseFilePath,
        string sessionId,
        long afterSequence,
        int maxCount,
        CancellationToken cancellationToken = default);

    Task<TaskSessionSummarySnapshot> SaveSummaryAsync(
        string databaseFilePath,
        string sessionId,
        string label,
        string markdown,
        CancellationToken cancellationToken = default);

    Task<TaskSessionSummarySnapshot> GetSummaryAsync(
        string databaseFilePath,
        string sessionId,
        string label,
        CancellationToken cancellationToken = default);
}
