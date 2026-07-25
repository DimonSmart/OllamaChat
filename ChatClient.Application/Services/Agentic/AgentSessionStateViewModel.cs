namespace ChatClient.Application.Services.Agentic;

public sealed record AgentSessionStateViewModel(
    string? Mode,
    IReadOnlyList<string> AvailableModes,
    bool HasTodoProvider,
    bool HasAgentModeProvider,
    IReadOnlyList<AgentSessionTodoItemViewModel> Todos);

public sealed record AgentSessionTodoItemViewModel(
    int Id,
    string Title,
    string? Description,
    bool IsComplete);
