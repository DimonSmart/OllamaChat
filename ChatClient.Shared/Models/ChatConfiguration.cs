namespace ChatClient.Shared.Models;

public record ChatConfiguration(
    string ModelName,
    IReadOnlyCollection<string> Functions,
    bool UseAgentResponses,
    bool AutoSelectFunctions = false,
    int AutoSelectCount = 0,
    int MaximumInvocationCount = 1);
