namespace ChatClient.Domain.Models;

public enum ToolInvocationStatus
{
    Running,
    Succeeded,
    Failed,
    Canceled
}

public sealed record ToolInvocationViewState(
    string CallId,
    string RegisteredName,
    string OriginalName,
    string Source,
    string ServerName,
    string? BindingName,
    bool IsInteractive,
    string Arguments,
    string? Result,
    string? Error,
    ToolInvocationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);
