using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Services.AgentRuntime;

public interface IWorkflowSessionState
{
    Task<string> CreateAsync(
        WorkflowSessionInitialization initialization,
        CancellationToken cancellationToken = default);

    Task<bool> IsCompletedAsync(
        string sessionId,
        AgentWorkflowExecutionDefinition execution,
        CancellationToken cancellationToken = default);

    Task<string?> TryGetSummaryAsync(
        string sessionId,
        string label,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowSessionState(TaskSessionStore taskSessionStore) : IWorkflowSessionState
{
    public async Task<string> CreateAsync(
        WorkflowSessionInitialization initialization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialization);

        var session = await taskSessionStore.CreateSessionAsync(
            initialization.Title,
            initialization.Description,
            cancellationToken);
        await taskSessionStore.SetPhaseAsync(session.SessionId, "intake", cancellationToken);

        foreach (var input in initialization.Inputs)
        {
            if (input.Document is { } document)
            {
                await taskSessionStore.AttachDocumentAsync(
                    session.SessionId, input.Key, document.Markdown, document.Title,
                    document.SourceFile, cancellationToken);
                continue;
            }

            if (input.Parameter is { } parameter)
            {
                await taskSessionStore.SetParameterAsync(
                    session.SessionId, input.Key, parameter.Kind, parameter.Value, cancellationToken);
            }
        }

        return session.SessionId;
    }

    public async Task<bool> IsCompletedAsync(
        string sessionId,
        AgentWorkflowExecutionDefinition execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(execution);

        var snapshot = await taskSessionStore.GetSessionAsync(sessionId, cancellationToken);

        return (!string.IsNullOrWhiteSpace(execution.CompletionPhase) &&
                string.Equals(snapshot.Phase, execution.CompletionPhase, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(execution.CompletionSummaryLabel) &&
                snapshot.Summaries.Any(summary =>
                    string.Equals(summary.Label, execution.CompletionSummaryLabel, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<string?> TryGetSummaryAsync(
        string sessionId,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        return (await taskSessionStore.TryGetSummaryAsync(sessionId, label, cancellationToken))?.Markdown;
    }
}

public sealed record WorkflowSessionInitialization(
    string? Title,
    string? Description,
    IReadOnlyList<WorkflowSessionInput> Inputs);

public sealed record WorkflowSessionInput(
    string Key,
    WorkflowSessionParameter? Parameter = null,
    WorkflowSessionDocument? Document = null);

public sealed record WorkflowSessionParameter(string Kind, string Value);

public sealed record WorkflowSessionDocument(
    string Markdown,
    string? Title,
    string? SourceFile);
