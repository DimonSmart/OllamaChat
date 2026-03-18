namespace ChatClient.Domain.Models;

public sealed class McpOverrideDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Kind { get; set; } = "string";

    public bool Required { get; set; }

    public bool Secret { get; set; }

    public string GetNormalizedKind()
    {
        return string.Equals(Kind, "int", StringComparison.OrdinalIgnoreCase)
            ? "int"
            : string.Equals(Kind, "bool", StringComparison.OrdinalIgnoreCase)
                ? "bool"
                : "string";
    }
}
