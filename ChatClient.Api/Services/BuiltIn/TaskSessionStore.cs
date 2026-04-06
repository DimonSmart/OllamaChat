using ChatClient.Application.Services.TaskSessions;

namespace ChatClient.Api.Services.BuiltIn;

public sealed class TaskSessionStore(
    McpServerSessionContext sessionContext,
    ITaskSessionRepository repository)
{
    public const string DatabaseFileParameter = "databaseFile";
    public const string SessionIdParameter = "sessionId";
    private const string DefaultDatabaseFile = "UserData/task-sessions.db";

    public TaskSessionContextInfo GetContext()
    {
        var storage = ResolveStorage();
        return new TaskSessionContextInfo(storage.DatabaseFilePath);
    }

    public Task<TaskSessionSnapshot> CreateSessionAsync(
        string? title,
        string? description,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.CreateSessionAsync(
            storage.DatabaseFilePath,
            Guid.NewGuid().ToString("N"),
            title,
            description,
            cancellationToken);
    }

    public Task<TaskSessionSnapshot> GetSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.GetSessionAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            cancellationToken);
    }

    public Task<TaskSessionSnapshot> SetPhaseAsync(
        string? sessionId,
        string phase,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.SetPhaseAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            NormalizeRequired(phase, "phase_required"),
            cancellationToken);
    }

    public Task<TaskSessionDocumentSnapshot> AttachDocumentAsync(
        string? sessionId,
        string kind,
        string markdown,
        string? title,
        string? source,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.UpsertDocumentAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            NormalizeRequired(kind, "document_kind_required"),
            NormalizeRequired(markdown, "document_markdown_required"),
            title,
            source,
            cancellationToken);
    }

    public Task<TaskSessionDocumentSnapshot> GetDocumentAsync(
        string? sessionId,
        string kind,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.GetDocumentAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            NormalizeRequired(kind, "document_kind_required"),
            cancellationToken);
    }

    public Task<TaskSessionParameterSnapshot> SetParameterAsync(
        string? sessionId,
        string key,
        string valueKind,
        string value,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.UpsertParameterAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            NormalizeRequired(key, "parameter_key_required"),
            NormalizeRequired(valueKind, "parameter_value_kind_required"),
            NormalizeRequired(value, "parameter_value_required"),
            cancellationToken);
    }

    public Task<TaskSessionParameterSnapshot> GetParameterAsync(
        string? sessionId,
        string key,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.GetParameterAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            NormalizeRequired(key, "parameter_key_required"),
            cancellationToken);
    }

    public Task<TaskSessionTurnSnapshot> AppendTurnAsync(
        string? sessionId,
        string role,
        string content,
        string? speakerId,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.AppendTurnAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            NormalizeRequired(role, "turn_role_required"),
            NormalizeRequired(content, "turn_content_required"),
            NormalizeOptional(speakerId),
            cancellationToken);
    }

    public Task<IReadOnlyList<TaskSessionTurnSnapshot>> ListTurnsAsync(
        string? sessionId,
        long? afterSequence,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.ListTurnsAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            Math.Max(0, afterSequence ?? 0),
            Math.Clamp(maxCount, 1, 200),
            cancellationToken);
    }

    public Task<TaskSessionSummarySnapshot> SaveSummaryAsync(
        string? sessionId,
        string label,
        string markdown,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.SaveSummaryAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            NormalizeRequired(label, "summary_label_required"),
            NormalizeRequired(markdown, "summary_markdown_required"),
            cancellationToken);
    }

    public Task<TaskSessionSummarySnapshot> GetSummaryAsync(
        string? sessionId,
        string label,
        CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        return repository.GetSummaryAsync(
            storage.DatabaseFilePath,
            ResolveExistingSessionId(sessionId),
            NormalizeRequired(label, "summary_label_required"),
            cancellationToken);
    }

    private TaskSessionStorage ResolveStorage()
    {
        var configuredPath = sessionContext.Binding?.Parameters.TryGetValue(DatabaseFileParameter, out var databaseFile) == true &&
                             !string.IsNullOrWhiteSpace(databaseFile)
            ? databaseFile
            : DefaultDatabaseFile;

        return new TaskSessionStorage(Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath!
                : Path.Combine(AppContext.BaseDirectory, configuredPath!)));
    }

    private string ResolveExistingSessionId(string? sessionId)
    {
        var bindingSessionId = sessionContext.Binding?.Parameters.TryGetValue(SessionIdParameter, out var configuredSessionId) == true
            ? configuredSessionId
            : null;

        return NormalizeRequired(
            string.IsNullOrWhiteSpace(sessionId) ? bindingSessionId : sessionId,
            "session_id_required");
    }

    private static string NormalizeRequired(string? value, string code)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(code);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record TaskSessionStorage(string DatabaseFilePath);
}
