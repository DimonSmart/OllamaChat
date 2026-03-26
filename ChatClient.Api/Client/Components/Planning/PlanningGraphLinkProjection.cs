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

    public string? DeclaredType { get; init; }

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
                CollectMatches(inputBinding.Key, inputBinding.Value, matchesBySource);
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

        if (finalResult is not null && steps.Count > 0)
        {
            descriptors.Add(new PlanningGraphLinkDescriptor
            {
                Id = PlanningGraphLinkDescriptor.CreateId(
                    steps[^1].Id,
                    PlanningVirtualNodeDescriptor.ResultNodeId,
                    PlanningGraphLinkKind.Result),
                SourceId = steps[^1].Id,
                TargetId = PlanningVirtualNodeDescriptor.ResultNodeId,
                Kind = PlanningGraphLinkKind.Result
            });
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
        IDictionary<string, List<PlanningGraphLinkMatch>> matchesBySource)
    {
        if (!PlanInputBindingSyntax.TryParseBinding(rootValue, out var bindingExpression, out var bindingError)
            || !string.IsNullOrWhiteSpace(bindingError)
            || bindingExpression is null)
        {
            return;
        }

        switch (bindingExpression)
        {
            case PlanInputBindingSpec binding:
                AddMatch(
                    inputName,
                    rootValue,
                    CreateBindingNode(binding),
                    inputName,
                    binding.From,
                    GetModeText(binding.Mode),
                    binding.Type,
                    matchesBySource);
                return;
            case PlanInputConcatBindingSpec concatBinding:
                for (var index = 0; index < concatBinding.Concat.Count; index++)
                {
                    var binding = concatBinding.Concat[index];
                    AddMatch(
                        inputName,
                        rootValue,
                        CreateBindingNode(binding),
                        $"{inputName}.concat[{index}]",
                        binding.From,
                        GetModeText(binding.Mode),
                        concatBinding.Type,
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
        string? declaredType,
        IDictionary<string, List<PlanningGraphLinkMatch>> matchesBySource)
    {
        if (!reference.StartsWith("$", StringComparison.Ordinal))
        {
            return;
        }

        var sourceId = PlanInputBindingSyntax.TryParseReference(reference, out var parsedReference, out _)
            ? parsedReference!.StepId
            : ExtractBaseStepId(reference[1..]);
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
            DeclaredType = declaredType,
            BindingJson = SerializeNode(rootValue),
            ReferenceJson = SerializeNode(currentValue)
        });
    }

    private static string SerializeNode(JsonNode? node) =>
        PlanningJson.SerializeNodeIndented(node);

    private static JsonObject CreateBindingNode(PlanInputBindingSpec binding)
    {
        var node = new JsonObject
        {
            ["from"] = binding.From,
            ["mode"] = GetModeText(binding.Mode)
        };

        if (!string.IsNullOrWhiteSpace(binding.Type))
            node["type"] = binding.Type;

        return node;
    }

    private static string GetModeText(PlanInputBindingMode mode) =>
        mode == PlanInputBindingMode.Map ? "map" : "value";
}
