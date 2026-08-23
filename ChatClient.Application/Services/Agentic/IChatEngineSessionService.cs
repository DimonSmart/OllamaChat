using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatEngineSessionService : IChatSessionService
{
    event Action? SessionStateChanged;

    bool HasActiveSession { get; }

    ActiveChatSessionInfo? ActiveSession { get; }

    ToolApprovalRequestViewModel? PendingToolApproval { get; }

    Task StartAsync(
        ChatEngineSessionStartRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ChatSessionStartProgress>? progress = null);

    Task<AgentSessionStateViewModel?> GetSessionStateAsync(CancellationToken cancellationToken = default);

    Task SetAgentModeAsync(string mode, CancellationToken cancellationToken = default);

    Task SetFileAccessWorkspaceAsync(string workspace, CancellationToken cancellationToken = default);

    Task<string?> ReadFileMemoryAsync(string name, CancellationToken cancellationToken = default);

    Task ClearFileMemoryAsync(CancellationToken cancellationToken = default);

    Task<string> ExportHarnessSessionAsync(CancellationToken cancellationToken = default);

    Task RestoreHarnessSessionAsync(string snapshotJson, CancellationToken cancellationToken = default);

    Task RestoreSavedChatAsync(
        SavedChatDocument chat,
        CancellationToken cancellationToken = default,
        IProgress<ChatSessionRestoreProgress>? progress = null);

    Task RespondToToolApprovalAsync(ToolApprovalDecision decision, CancellationToken cancellationToken = default);
}

public sealed record ActiveChatSessionInfo(
    AgentDefinitionReference RuntimeReference,
    ServerModel? Model,
    IReadOnlyDictionary<string, string> Inputs,
    AgentSessionOverrides Overrides);

public enum ChatSessionStartStage
{
    ResettingPreviousSession,
    ResolvingDefinition,
    PreparingRuntime,
    CheckingSandboxAvailability,
    StartingSandbox,
    VerifyingSandbox,
    CreatingAgentSession
}

public sealed record ChatSessionStartProgress(
    ChatSessionStartStage Stage,
    string Message);

public enum ChatSessionRestoreStage
{
    ValidatingSavedChat,
    ResolvingDefinition,
    CheckingSandboxAvailability,
    StartingSandbox,
    VerifyingSandbox,
    RestoringAgentSession,
    RestoringConversation
}

public sealed record ChatSessionRestoreProgress(
    ChatSessionRestoreStage Stage,
    string Message);
