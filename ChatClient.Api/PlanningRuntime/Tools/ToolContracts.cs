using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Common;

namespace ChatClient.Api.PlanningRuntime.Tools;

public sealed record ToolPlannerMetadata(
    string Name,
    string Description,
    JsonObject InputSchema,
    JsonObject OutputSchema,
    string[] Tags,
    string[] SupportedConsume)
{
    public bool HasTag(string tag)
    {
        return Tags.Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
    }
}

public interface ITool
{
    string Name { get; }

    ToolPlannerMetadata PlannerMetadata { get; }

    Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default);
}

public interface IToolRegistry
{
    ITool GetRequired(string name);

    IReadOnlyCollection<ToolPlannerMetadata> ListPlannerMetadata();
}

public sealed class ToolRegistry(IEnumerable<ITool> tools) : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    public ITool GetRequired(string name)
    {
        if (_tools.TryGetValue(name, out var tool))
        {
            return tool;
        }

        throw new InvalidOperationException($"Tool '{name}' is not registered.");
    }

    public IReadOnlyCollection<ToolPlannerMetadata> ListPlannerMetadata() =>
        _tools.Values.Select(x => x.PlannerMetadata).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
}

