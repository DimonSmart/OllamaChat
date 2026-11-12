namespace ChatClient.Application.Services.AgentRuntime;

/// <summary>
/// Immutable, root-execution-scoped capability ceiling. An agent definition or
/// provider profile can only reduce these permissions, never increase them.
/// </summary>
public enum AgentExecutionMode
{
    Interactive,
    ScheduledRestricted,
    CliNonInteractive
}

public sealed record AgentExecutionPolicy
{
    public AgentExecutionMode Mode { get; init; } = AgentExecutionMode.Interactive;
    public bool AllowRead { get; init; }
    public bool AllowWrite { get; init; }
    public bool AllowShell { get; init; }
    public bool AllowFileMemory { get; init; }
    public static AgentExecutionPolicy ScheduledRestricted { get; } =
        new() { Mode = AgentExecutionMode.ScheduledRestricted, AllowRead = true };

    public static AgentExecutionPolicy Cli(
        bool allowRead, bool allowWrite, bool allowShell, bool allowFileMemory) => new()
    {
        Mode = AgentExecutionMode.CliNonInteractive,
        AllowRead = allowRead || allowWrite,
        AllowWrite = allowWrite,
        AllowShell = allowShell,
        AllowFileMemory = allowFileMemory
    };
}
