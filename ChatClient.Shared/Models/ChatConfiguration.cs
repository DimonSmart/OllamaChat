namespace ChatClient.Shared.Models;

public record ChatConfiguration(
    string ModelName,
    IReadOnlyCollection<string> Functions);
