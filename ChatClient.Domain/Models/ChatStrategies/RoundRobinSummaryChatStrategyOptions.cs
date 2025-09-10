namespace ChatClient.Domain.Models.ChatStrategies;

public class RoundRobinSummaryChatStrategyOptions : IChatStrategyOptions
{
    public int Rounds { get; set; } = 1;
    public string SummaryAgent { get; set; } = string.Empty;
}
