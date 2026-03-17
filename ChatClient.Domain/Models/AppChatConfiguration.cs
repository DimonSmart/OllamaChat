namespace ChatClient.Domain.Models;

public record AppChatConfiguration
{
    public AppChatConfiguration(
        string modelName,
        IReadOnlyCollection<string> functions,
        bool useWhiteboard = true,
        IReadOnlyCollection<McpServerSessionBinding>? mcpServerBindings = null)
    {
        ModelName = modelName;
        Functions = functions;
        UseWhiteboard = useWhiteboard;
        McpServerBindings = mcpServerBindings ?? [];
    }

    public string ModelName { get; init; }

    public IReadOnlyCollection<string> Functions { get; init; }

    public bool UseWhiteboard { get; init; } = true;

    public IReadOnlyCollection<McpServerSessionBinding> McpServerBindings { get; init; } = [];

    public override string ToString()
    {
        var functionList = Functions?.Count > 0 ? string.Join(", ", Functions) : "none";
        return $"{{ ModelName = {ModelName}, Functions = [{functionList}] (Count: {Functions?.Count ?? 0}), UseWhiteboard = {UseWhiteboard}, McpBindings = {McpServerBindings.Count} }}";
    }
}
