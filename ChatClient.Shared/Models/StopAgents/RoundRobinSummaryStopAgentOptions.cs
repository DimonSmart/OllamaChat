namespace ChatClient.Shared.Models.StopAgents;

public class RoundRobinSummaryStopAgentOptions : IStopAgentOptions
{
    public int Rounds { get; set; } = 1;
    public string SummaryAgent { get; set; } = string.Empty;
}
