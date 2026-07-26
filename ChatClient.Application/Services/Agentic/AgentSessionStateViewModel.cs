namespace ChatClient.Application.Services.Agentic;

using ChatClient.Domain.Models;

public sealed record AgentSessionStateViewModel(
    string? Mode,
    IReadOnlyList<string> AvailableModes,
    bool HasTodoProvider,
    bool HasAgentModeProvider,
    IReadOnlyList<AgentSessionTodoItemViewModel> Todos,
    AgentSessionFileAccessViewModel? FileAccess = null);

public sealed record AgentSessionTodoItemViewModel(
    int Id,
    string Title,
    string? Description,
    bool IsComplete);

public sealed record AgentSessionFileAccessViewModel(
    string WorkspacePath,
    string ProfileName,
    FileAccessMode AccessMode,
    bool RequireReadApproval,
    bool RequireWriteApproval);
