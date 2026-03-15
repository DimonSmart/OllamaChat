using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.Client.Components.Planning;

public enum PlanningGraphLinkKind
{
    Dependency,
    Result
}

public sealed record PlanningGraphLinkMatch
{
    public required string InputName { get; init; }

    public required string Path { get; init; }

    public required string Reference { get; init; }

    public string? Mode { get; init; }

    public required string BindingJson { get; init; }

    public required string ReferenceJson { get; init; }
}

public sealed record PlanningGraphLinkDescriptor
{
    public const string Prefix = "__link__:";

    public required string Id { get; init; }

    public required string SourceId { get; init; }

    public required string TargetId { get; init; }

    public required PlanningGraphLinkKind Kind { get; init; }

    public IReadOnlyList<PlanningGraphLinkMatch> Matches { get; init; } = [];

    public static string CreateId(string sourceId, string targetId, PlanningGraphLinkKind kind) =>
        $"{Prefix}{kind.ToString().ToLowerInvariant()}:{sourceId}->{targetId}";
}

public static class PlanningGraphLinkProjection
{
    public static IReadOnlyList<PlanningGraphLinkDescriptor> Build(
        IReadOnlyList<PlanStep> steps,
        ResultEnvelope<JsonElement?>? finalResult)
    {
        var descriptors = new List<PlanningGraphLinkDescriptor>();

        foreach (var targetStep in steps)
        {
            var matchesBySource = new Dictionary<string, List<PlanningGraphLinkMatch>>(StringComparer.Ordinal);

            foreach (var inputBinding in targetStep.In)
            {
                CollectMatches(
                    inputBinding.Key,
                    inputBinding.Value,
                    inputBinding.Value,
                    inputBinding.Key,
                    matchesBySource);
            }

            foreach (var (sourceId, matches) in matchesBySource)
            {
                descriptors.Add(new PlanningGraphLinkDescriptor
                {
                    Id = PlanningGraphLinkDescriptor.CreateId(sourceId, targetStep.Id, PlanningGraphLinkKind.Dependency),
                    SourceId = sourceId,
                    TargetId = targetStep.Id,
                    Kind = PlanningGraphLinkKind.Dependency,
                    Matches = matches
                });
            }
        }

        if (finalResult is not null)
        {
            foreach (var terminalStepId in GetTerminalStepIds(steps))
            {
                descriptors.Add(new PlanningGraphLinkDescriptor
                {
                    Id = PlanningGraphLinkDescriptor.CreateId(
                        terminalStepId,
                        PlanningVirtualNodeDescriptor.ResultNodeId,
                        PlanningGraphLinkKind.Result),
                    SourceId = terminalStepId,
                    TargetId = PlanningVirtualNodeDescriptor.ResultNodeId,
                    Kind = PlanningGraphLinkKind.Result
                });
            }
        }

        return descriptors;
    }

    public static string ExtractBaseStepId(string expression)
    {
        var bracketIndex = expression.IndexOf('[');
        var dotIndex = expression.IndexOf('.');
        if (bracketIndex >= 0 && dotIndex >= 0)
            return expression[..Math.Min(bracketIndex, dotIndex)];
        if (bracketIndex >= 0)
            return expression[..bracketIndex];
        if (dotIndex >= 0)
            return expression[..dotIndex];
        return expression;
    }

    private static void CollectMatches(
        string inputName,
        JsonNode? rootValue,
        JsonNode? currentValue,
        string path,
        IDictionary<string, List<PlanningGraphLinkMatch>> matchesBySource)
    {
        if (currentValue is null)
        {
            return;
        }

        if (currentValue is JsonObject obj && TryReadReference(obj, out var reference, out var mode))
        {
            AddMatch(
                inputName,
                rootValue,
                currentValue,
                path,
                reference,
                mode,
                matchesBySource);
            return;
        }

        switch (currentValue)
        {
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) &&
                                         text.StartsWith("$", StringComparison.Ordinal):
                AddMatch(
                    inputName,
                    rootValue,
                    currentValue,
                    path,
                    text,
                    mode: null,
                    matchesBySource);
                return;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    CollectMatches(
                        inputName,
                        rootValue,
                        array[index],
                        $"{path}[{index}]",
                        matchesBySource);
                }

                return;

            case JsonObject nestedObject:
                foreach (var property in nestedObject)
                {
                    CollectMatches(
                        inputName,
                        rootValue,
                        property.Value,
                        $"{path}.{property.Key}",
                        matchesBySource);
                }

                return;
        }
    }

    private static void AddMatch(
        string inputName,
        JsonNode? rootValue,
        JsonNode currentValue,
        string path,
        string reference,
        string? mode,
        IDictionary<string, List<PlanningGraphLinkMatch>> matchesBySource)
    {
        if (!reference.StartsWith("$", StringComparison.Ordinal))
        {
            return;
        }

        var sourceId = ExtractBaseStepId(reference[1..]);
        if (!matchesBySource.TryGetValue(sourceId, out var matches))
        {
            matches = [];
            matchesBySource[sourceId] = matches;
        }

        matches.Add(new PlanningGraphLinkMatch
        {
            InputName = inputName,
            Path = path,
            Reference = reference,
            Mode = mode,
            BindingJson = SerializeNode(rootValue),
            ReferenceJson = SerializeNode(currentValue)
        });
    }

    private static bool TryReadReference(JsonObject obj, out string reference, out string? mode)
    {
        reference = string.Empty;
        mode = null;

        if (!obj.TryGetPropertyValue("from", out var fromNode) ||
            fromNode is not JsonValue fromValue ||
            !fromValue.TryGetValue<string>(out var parsedReference) ||
            string.IsNullOrWhiteSpace(parsedReference) ||
            !parsedReference.StartsWith("$", StringComparison.Ordinal))
        {
            return false;
        }

        reference = parsedReference;

        if (obj.TryGetPropertyValue("mode", out var modeNode) &&
            modeNode is JsonValue modeValue &&
            modeValue.TryGetValue<string>(out var parsedMode))
        {
            mode = parsedMode;
        }

        return true;
    }

    private static string SerializeNode(JsonNode? node) =>
        PlanningJson.SerializeNodeIndented(node);

    private static IReadOnlyList<string> GetTerminalStepIds(IReadOnlyList<PlanStep> steps)
    {
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            foreach (var inputValue in step.In.Values)
            {
                CollectDependencyIds(inputValue, dependencyIds);
            }
        }

        return steps
            .Where(step => !dependencyIds.Contains(step.Id))
            .Select(step => step.Id)
            .ToList();
    }

    private static void CollectDependencyIds(JsonNode? value, ISet<string> dependencyIds)
    {
        if (value is null)
        {
            return;
        }

        if (value is JsonObject obj && TryReadReference(obj, out var reference, out _))
        {
            dependencyIds.Add(ExtractBaseStepId(reference[1..]));
            return;
        }

        switch (value)
        {
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) &&
                                         text.StartsWith("$", StringComparison.Ordinal):
                dependencyIds.Add(ExtractBaseStepId(text[1..]));
                return;

            case JsonArray array:
                foreach (var item in array)
                {
                    CollectDependencyIds(item, dependencyIds);
                }

                return;

            case JsonObject nestedObject:
                foreach (var property in nestedObject)
                {
                    CollectDependencyIds(property.Value, dependencyIds);
                }

                return;
        }
    }
}
