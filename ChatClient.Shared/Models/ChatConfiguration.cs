namespace ChatClient.Shared.Models;

public record ChatConfiguration(
    string ModelName,
    IReadOnlyCollection<string> Functions,
    int MaximumInvocationCount = 1,
    string StopAgentName = "",
    string StopPhrase = "");
