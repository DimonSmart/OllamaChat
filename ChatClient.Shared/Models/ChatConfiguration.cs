namespace ChatClient.Shared.Models;

public record ChatConfiguration(
    string ModelName,
    IReadOnlyCollection<string> Functions,
    bool UseAgentMode,
    bool AutoSelectFunctions = false,
    int AutoSelectCount = 0);
