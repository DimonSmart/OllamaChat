namespace ChatClient.Shared.Models;

public record ChatConfiguration(
    string ModelName,
    IReadOnlyCollection<string> Functions,
    bool UseAgentResponses,
    int MaximumInvocationCount = 1,
    string StopAgentName = "",
    string StopPhrase = "");
