namespace ChatClient.Domain.Models;

public static class AgentHistoryCompactionModes
{
    public const string None = "none";
    public const string ToolWindow = "tool_window";
}

public sealed class AgentHistoryCompactionSettings
{
    public bool Enabled { get; set; }

    public string Mode { get; set; } = AgentHistoryCompactionModes.None;

    public List<string> ToolNames { get; set; } = [];

    public int KeepLastToolPairs { get; set; }
}

public sealed class AgentExecutionSettings
{
    public AgentHistoryCompactionSettings HistoryCompaction { get; set; } = new();

    public int? MaxToolCalls { get; set; }
}
