namespace ChatClient.Application.Services.Agentic;

public abstract record HarnessResponseEvent;

public sealed record HarnessTextDelta(string Text) : HarnessResponseEvent;

public abstract record HarnessToolCallEvent(
    string CallId,
    string RegisteredName,
    string OriginalName,
    string Source,
    string ServerName,
    string? BindingName,
    bool IsInteractive,
    string Arguments,
    DateTimeOffset StartedAt) : HarnessResponseEvent;

public sealed record HarnessToolCallStarted(
    string CallId,
    string RegisteredName,
    string OriginalName,
    string Source,
    string ServerName,
    string? BindingName,
    bool IsInteractive,
    string Arguments,
    DateTimeOffset StartedAt)
    : HarnessToolCallEvent(CallId, RegisteredName, OriginalName, Source, ServerName, BindingName, IsInteractive, Arguments, StartedAt);

public sealed record HarnessToolCallCompleted(
    string CallId,
    string RegisteredName,
    string OriginalName,
    string Source,
    string ServerName,
    string? BindingName,
    bool IsInteractive,
    string Arguments,
    string Result,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
    : HarnessToolCallEvent(CallId, RegisteredName, OriginalName, Source, ServerName, BindingName, IsInteractive, Arguments, StartedAt);

public sealed record HarnessToolCallFailed(
    string CallId,
    string RegisteredName,
    string OriginalName,
    string Source,
    string ServerName,
    string? BindingName,
    bool IsInteractive,
    string Arguments,
    string Error,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
    : HarnessToolCallEvent(CallId, RegisteredName, OriginalName, Source, ServerName, BindingName, IsInteractive, Arguments, StartedAt);

public sealed record HarnessResponseMetadata(string? ResponseId, string? ModelId) : HarnessResponseEvent;
