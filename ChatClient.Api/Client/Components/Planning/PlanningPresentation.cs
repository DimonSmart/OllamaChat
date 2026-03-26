using System.Text;
using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.Client.Components.Planning;

public static class PlanningStepPresentation
{
    public static string GetCompactName(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var rawName = step.CapabilityId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawName))
            return string.Empty;

        return PlanStepKinds.GetKind(step) switch
        {
            PlanStepKinds.Tool => GetCompactToolName(rawName),
            _ => rawName
        };
    }

    public static string GetCompactKind(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return PlanStepKinds.GetKind(step);
    }

    public static string GetNodeMetaText(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var kind = GetCompactKind(step);
        var name = GetCompactName(step);

        if (string.IsNullOrWhiteSpace(kind))
            return name;

        if (string.IsNullOrWhiteSpace(name))
            return kind;

        return $"{kind}: {name}";
    }

    public static string GetCompactToolName(string? qualifiedToolName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedToolName))
            return string.Empty;

        var trimmed = qualifiedToolName.Trim();
        if (trimmed.StartsWith("binding:", StringComparison.OrdinalIgnoreCase))
        {
            var secondColonIndex = trimmed.IndexOf(':', "binding:".Length);
            if (secondColonIndex >= 0 && secondColonIndex + 1 < trimmed.Length)
            {
                return trimmed[(secondColonIndex + 1)..].Trim();
            }
        }

        var lastColonIndex = trimmed.LastIndexOf(':');
        if (lastColonIndex >= 0 && lastColonIndex + 1 < trimmed.Length)
        {
            return trimmed[(lastColonIndex + 1)..].Trim();
        }

        return trimmed;
    }
}

public static class PlanningLinkPresentation
{
    public static string GetBindingSummary(PlanningGraphLinkMatch match, JsonElement? resolvedValue = null)
    {
        ArgumentNullException.ThrowIfNull(match);

        var inputLabel = string.Equals(match.Path, match.InputName, StringComparison.Ordinal)
            ? match.InputName
            : match.Path;

        return $"{GetSourcePath(match, resolvedValue)} -> {inputLabel}";
    }

    public static string GetSourcePath(PlanningGraphLinkMatch match, JsonElement? resolvedValue = null)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (!PlanInputBindingSyntax.TryParseReference(match.Reference, out var parsedReference, out _)
            || parsedReference is null)
        {
            return match.Reference.Trim().TrimStart('$');
        }

        var builder = new StringBuilder();
        foreach (var segment in parsedReference.Segments)
        {
            switch (segment.Kind)
            {
                case StepReferenceSegmentKind.Property:
                    if (builder.Length > 0)
                        builder.Append('.');
                    builder.Append(segment.PropertyName);
                    break;
                case StepReferenceSegmentKind.ArrayAny:
                    builder.Append("[]");
                    break;
                case StepReferenceSegmentKind.ArrayIndex:
                    builder.Append('[');
                    builder.Append(segment.Index);
                    builder.Append(']');
                    break;
            }
        }

        if (builder.Length == 0)
            builder.Append("root");

        if (ShouldAppendMappedArraySuffix(parsedReference, match.Mode, resolvedValue))
        {
            builder.Append("[]");
        }

        return builder.ToString();
    }

    public static string GetShapeLabel(PlanningGraphLinkMatch match, JsonElement? resolvedValue = null)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (resolvedValue is JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.Array => $"array({value.GetArrayLength()})",
                JsonValueKind.Object => "object",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => "value"
            };
        }

        if (string.Equals(match.Mode, "map", StringComparison.OrdinalIgnoreCase))
            return "array";

        if (!PlanInputBindingSyntax.TryParseReference(match.Reference, out var parsedReference, out _)
            || parsedReference is null)
        {
            return "value";
        }

        return parsedReference.Segments.LastOrDefault() is { Kind: StepReferenceSegmentKind.ArrayAny }
            ? "array"
            : "value";
    }

    public static string GetModeLabel(string? mode) =>
        string.Equals(mode, "map", StringComparison.OrdinalIgnoreCase)
            ? "map"
            : "value";

    private static bool ShouldAppendMappedArraySuffix(
        ParsedStepReference parsedReference,
        string? mode,
        JsonElement? resolvedValue)
    {
        if (!string.Equals(mode, "map", StringComparison.OrdinalIgnoreCase)
            || resolvedValue is not JsonElement value
            || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return !parsedReference.Segments.Any(static segment =>
            segment.Kind is StepReferenceSegmentKind.ArrayAny or StepReferenceSegmentKind.ArrayIndex);
    }
}
