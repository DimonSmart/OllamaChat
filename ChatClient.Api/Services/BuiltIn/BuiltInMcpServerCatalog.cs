using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.BuiltIn;

public static class BuiltInMcpServerCatalog
{
    public static IReadOnlyList<IBuiltInMcpServerDescriptor> Definitions { get; } = [
        BuiltInTimeServerTools.Descriptor,
        BuiltInFormattedTimeServerTools.Descriptor,
        BuiltInMathServerTools.Descriptor,
        BuiltInUserProfilePrefsServerTools.Descriptor
    ];

    public static bool TryGetDefinition(string key, out IBuiltInMcpServerDescriptor? definition)
    {
        definition = Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }

    public static McpServerConfig CreateConfig(IBuiltInMcpServerDescriptor definition, DateTime nowUtc)
    {
        return new McpServerConfig
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
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

        return Definitions.Any(d => d.Id == value);
    }
}
