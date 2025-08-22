namespace ChatClient.Shared.Models;

public record AppChatConfiguration(
    string ModelName,
    IReadOnlyCollection<string> Functions);
