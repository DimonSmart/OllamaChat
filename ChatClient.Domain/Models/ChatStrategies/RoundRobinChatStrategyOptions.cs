namespace ChatClient.Domain.Models.ChatStrategies;

public class RoundRobinChatStrategyOptions : IChatStrategyOptions
{
    public int Rounds { get; set; } = 1;
}
