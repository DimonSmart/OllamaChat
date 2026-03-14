using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Tools;

public sealed class PlanningToolCatalog(IEnumerable<AppToolDescriptor> tools)
{
    private readonly Dictionary<string, AppToolDescriptor> _tools = tools.ToDictionary(
        static tool => tool.QualifiedName,
        StringComparer.OrdinalIgnoreCase);

    public AppToolDescriptor GetRequired(string name)
    {
        if (_tools.TryGetValue(name, out var tool))
            return tool;

        throw new InvalidOperationException($"Tool '{name}' is not registered.");
    }

    public IReadOnlyCollection<AppToolDescriptor> ListTools() =>
        _tools.Values
            .OrderBy(static tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
