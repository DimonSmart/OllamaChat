namespace ChatClient.Domain.Models;

public record AppChatConfiguration
{
    public AppChatConfiguration(
        string modelName,
        IReadOnlyCollection<string> functions,
        IReadOnlyCollection<McpServerSessionBinding>? mcpServerBindings = null)
    {
        ModelName = modelName;
        Functions = functions;
        McpServerBindings = mcpServerBindings ?? [];
    }

    public string ModelName { get; init; }

    public IReadOnlyCollection<string> Functions { get; init; }

    public IReadOnlyCollection<McpServerSessionBinding> McpServerBindings { get; init; } = [];

    public override string ToString()
    {
        var functionList = Functions?.Count > 0 ? string.Join(", ", Functions) : "none";
        return $"{{ ModelName = {ModelName}, Functions = [{functionList}] (Count: {Functions?.Count ?? 0}), McpBindings = {McpServerBindings.Count} }}";
    }
}
