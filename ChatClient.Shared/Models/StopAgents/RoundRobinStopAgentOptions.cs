namespace ChatClient.Shared.Models.StopAgents;

public class RoundRobinStopAgentOptions : IStopAgentOptions
{
    public int Rounds { get; set; } = 1;
}
