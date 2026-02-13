using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.BuiltIn;

public sealed record BuiltInMcpServerDefinition(Guid Id, string Key, string Name, string Description);

public static class BuiltInMcpServerCatalog
{
    public const string TimeServerKey = "built-in-time";
    public const string FormattedTimeServerKey = "built-in-formatted-time";
    public const string MathServerKey = "built-in-math";

    private static readonly IReadOnlyList<BuiltInMcpServerDefinition> _definitions =
    [
        new(
            Id: Guid.Parse("f2f13fdb-09e4-46b8-9e2e-352c3da66f20"),
            Key: TimeServerKey,
            Name: "Built-in Time MCP Server",
            Description: "Returns current time information."),
        new(
            Id: Guid.Parse("1b44ff82-c4fc-4f50-a12f-56429817c078"),
            Key: FormattedTimeServerKey,
            Name: "Built-in Formatted Time MCP Server",
            Description: "Returns current time in a custom format and asks user for timezone when needed."),
        new(
            Id: Guid.Parse("76ca15c0-4f2d-4a76-8d32-70fdd6dd5083"),
            Key: MathServerKey,
            Name: "Built-in Math MCP Server",
            Description: "Evaluates arithmetic expressions from text input.")
    ];

    public static IReadOnlyList<BuiltInMcpServerDefinition> Definitions => _definitions;

    public static bool TryGetDefinition(string? key, out BuiltInMcpServerDefinition? definition)
    {
        definition = _definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }

    public static McpServerConfig CreateConfig(BuiltInMcpServerDefinition definition, DateTime nowUtc)
    {
        return new McpServerConfig
        {
            Id = definition.Id,
            Name = definition.Name,
            IsBuiltIn = true,
            BuiltInKey = definition.Key,
            Command = null,
            Arguments = null,
            Sse = null,
            SamplingModel = null,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };
    }

    public static bool IsBuiltInId(Guid? id)
    {
        if (id is not Guid value)
            return false;

        return _definitions.Any(d => d.Id == value);
    }
}
