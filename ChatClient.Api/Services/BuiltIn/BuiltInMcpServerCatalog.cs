namespace ChatClient.Api.Services.BuiltIn;

public static class BuiltInMcpServerCatalog
{
    public static IReadOnlyList<IBuiltInMcpServerDescriptor> Definitions { get; } = [
        BuiltInTimeServerTools.Descriptor,
        BuiltInFormattedTimeServerTools.Descriptor,
        BuiltInMathServerTools.Descriptor,
        BuiltInUserProfilePrefsServerTools.Descriptor
    ];

    public static bool TryGetDefinition(string? key, out IBuiltInMcpServerDescriptor? definition)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            definition = null;
            return false;
        }

        definition = Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }

    public static bool TryGetDefinition(Guid id, out IBuiltInMcpServerDescriptor? definition)
    {
        definition = Definitions.FirstOrDefault(d => d.Id == id);
        return definition is not null;
    }

    public static bool IsBuiltInId(Guid? id)
    {
        if (id is not Guid value)
            return false;

        return Definitions.Any(d => d.Id == value);
    }
}
