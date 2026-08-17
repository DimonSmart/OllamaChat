namespace ChatClient.Application.Services.Agentic;

using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;

public sealed record AgentSessionStateViewModel(
    string? Mode,
    IReadOnlyList<string> AvailableModes,
    bool HasTodoProvider,
    bool HasAgentModeProvider,
    IReadOnlyList<AgentSessionTodoItemViewModel> Todos,
    AgentSessionFileAccessViewModel? FileAccess = null,
    AgentSessionFileMemoryViewModel? FileMemory = null,
    AgentSessionSandboxViewModel? Sandbox = null,
    AgentSessionCompactionViewModel? Compaction = null,
    IReadOnlyList<AgentSessionSkillViewModel>? Skills = null,
    IReadOnlyList<string>? SkillDiagnostics = null,
    IReadOnlyList<AgentSessionBackgroundAgentViewModel>? BackgroundAgents = null);

public sealed record AgentSessionBackgroundAgentViewModel(string Name, string Summary, string? ModelName);

public sealed record AgentSessionSkillViewModel(string Name, string Description, string SourcePath, AgentSkillSourceKind SourceKind);

public sealed record AgentSessionCompactionViewModel(
    string ProfileName,
    int InputBudgetTokens,
    string PolicySummary);

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

public sealed record AgentSessionFileMemoryViewModel(
    string WorkingFolder,
    IReadOnlyList<AgentSessionFileMemoryEntryViewModel> Files);

public sealed record AgentSessionFileMemoryEntryViewModel(string Name, string? Description);

public sealed record AgentSessionSandboxViewModel(
    Guid ProfileId,
    string ProfileName,
    string ProviderType,
    string Image,
    string WorkspacePath,
    SandboxState State);
