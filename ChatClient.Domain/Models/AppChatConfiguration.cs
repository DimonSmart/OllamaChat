namespace ChatClient.Domain.Models;

public record AppChatConfiguration(
    string ModelName,
    IReadOnlyCollection<string> Functions,
    bool UseWhiteboard = true)
{
    public override string ToString()
    {
        var functionList = Functions?.Count > 0 ? string.Join(", ", Functions) : "none";
        return $"{{ ModelName = {ModelName}, Functions = [{functionList}] (Count: {Functions?.Count ?? 0}), UseWhiteboard = {UseWhiteboard} }}";
    }
};
