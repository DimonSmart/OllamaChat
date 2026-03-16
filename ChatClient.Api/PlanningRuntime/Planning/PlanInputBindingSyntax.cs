using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Planning;

public enum PlanInputBindingMode
{
    Value,
    Map
}

public sealed record PlanInputBindingSpec(
    string From,
    PlanInputBindingMode Mode,
    string? Type = null);

public enum StepReferenceSegmentKind
{
    Property,
    ArrayAny,
    ArrayIndex
}

public sealed record StepReferenceSegment(
    StepReferenceSegmentKind Kind,
    string? PropertyName = null,
    int? Index = null);

public sealed record ParsedStepReference(
    string StepId,
    IReadOnlyList<StepReferenceSegment> Segments);

public static class PlanInputBindingSyntax
{
    public static bool TryGetLegacyStringReference(JsonNode? node, out string? reference)
    {
        reference = null;

        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (!TryParseReference(text, out _, out _))
            return false;

        reference = text;
        return true;
    }

    public static bool TryParseBinding(JsonNode? node, out PlanInputBindingSpec? binding, out string? error)
    {
        binding = null;
        error = null;

        if (node is not JsonObject obj || !obj.ContainsKey("from"))
            return false;

        if (obj.Count is < 1 or > 3)
        {
            error = "Binding objects may contain only 'from' and optional 'mode' and 'type'.";
            return true;
        }

        if (!obj.TryGetPropertyValue("from", out var fromNode)
            || fromNode is not JsonValue fromValue
            || !fromValue.TryGetValue<string>(out var from)
            || string.IsNullOrWhiteSpace(from))
        {
            error = "Binding object field 'from' must be a non-empty string.";
            return true;
        }

        var mode = PlanInputBindingMode.Value;
        if (obj.TryGetPropertyValue("mode", out var modeNode) && modeNode is not null)
        {
            if (modeNode is not JsonValue modeValue
                || !modeValue.TryGetValue<string>(out var modeText)
                || string.IsNullOrWhiteSpace(modeText))
            {
                error = "Binding object field 'mode' must be 'value' or 'map'.";
                return true;
            }

            switch (modeText.Trim().ToLowerInvariant())
            {
                case "value":
                    mode = PlanInputBindingMode.Value;
                    break;
                case "map":
                    mode = PlanInputBindingMode.Map;
                    break;
                default:
                    error = "Binding object field 'mode' must be 'value' or 'map'.";
                    return true;
            }
        }

        string? declaredType = null;
        if (obj.TryGetPropertyValue("type", out var typeNode) && typeNode is not null)
        {
            if (typeNode is not JsonValue typeValue
                || !typeValue.TryGetValue<string>(out var typeText)
                || string.IsNullOrWhiteSpace(typeText))
            {
                error = "Binding object field 'type' must be a non-empty string when provided.";
                return true;
            }

            if (!StepInputTypeValidator.TryParse(typeText, out _, out var typeError))
            {
                error = $"Binding object field 'type' is invalid. {typeError}";
                return true;
            }

            declaredType = typeText.Trim();
        }

        binding = new PlanInputBindingSpec(from.Trim(), mode, declaredType);
        return true;
    }

    public static bool TryParseReference(string expression, out ParsedStepReference? reference, out string? error)
    {
        reference = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Reference cannot be empty.";
            return false;
        }

        if (!expression.StartsWith('$'))
        {
            error = "Reference must start with '$'.";
            return false;
        }

        var span = expression.AsSpan(1);
        var position = 0;
        var stepId = ParseIdentifier(span, ref position);
        if (stepId is null)
        {
            error = $"Invalid reference '{expression}'.";
            return false;
        }

        var segments = new List<StepReferenceSegment>();
        while (position < span.Length)
        {
            var current = span[position];
            if (current == '.')
            {
                position++;
                var property = ParseIdentifier(span, ref position);
                if (property is null)
                {
                    error = $"Invalid reference '{expression}'.";
                    return false;
                }

                segments.Add(new StepReferenceSegment(StepReferenceSegmentKind.Property, PropertyName: property));
                continue;
            }

            if (current == '[')
            {
                position++;
                if (position >= span.Length)
                {
                    error = $"Invalid reference '{expression}'.";
                    return false;
                }

                if (span[position] == ']')
                {
                    position++;
                    segments.Add(new StepReferenceSegment(StepReferenceSegmentKind.ArrayAny));
                    continue;
                }

                var indexStart = position;
                while (position < span.Length && char.IsDigit(span[position]))
                    position++;

                if (indexStart == position || position >= span.Length || span[position] != ']')
                {
                    error = $"Invalid reference '{expression}'.";
                    return false;
                }

                var indexText = span[indexStart..position];
                position++;
                if (!int.TryParse(indexText, out var index))
                {
                    error = $"Invalid reference '{expression}'.";
                    return false;
                }

                segments.Add(new StepReferenceSegment(StepReferenceSegmentKind.ArrayIndex, Index: index));
                continue;
            }

            error = $"Invalid reference '{expression}'.";
            return false;
        }

        reference = new ParsedStepReference(stepId, segments);
        return true;
    }

    public static JsonElement EvaluateReferenceOrThrow(
        string expression,
        string currentStepId,
        IReadOnlyDictionary<string, PlanStep> stepMap)
    {
        if (!TryParseReference(expression, out var reference, out var parseError))
            throw new InvalidOperationException($"Step '{currentStepId}': {parseError}");

        if (!stepMap.TryGetValue(reference!.StepId, out var referencedStep))
            throw new InvalidOperationException($"Step '{currentStepId}': ref '{expression}' - step '{reference.StepId}' not found.");

        if (!PlanExecutionState.IsDone(referencedStep) || referencedStep.Result is null)
            throw new InvalidOperationException($"Step '{currentStepId}': ref '{expression}' - step '{reference.StepId}' has no completed result.");

        var current = referencedStep.Result.Value.Clone();
        foreach (var segment in reference.Segments)
        {
            current = segment.Kind switch
            {
                StepReferenceSegmentKind.Property => EvaluatePropertySegment(current, segment.PropertyName!, currentStepId, expression),
                StepReferenceSegmentKind.ArrayAny => EvaluateArrayAnySegment(current, currentStepId, expression),
                StepReferenceSegmentKind.ArrayIndex => EvaluateArrayIndexSegment(current, segment.Index!.Value, currentStepId, expression),
                _ => throw new InvalidOperationException($"Step '{currentStepId}': ref '{expression}' - unsupported segment.")
            };
        }

        return current;
    }

    private static string? ParseIdentifier(ReadOnlySpan<char> span, ref int position)
    {
        if (position >= span.Length)
            return null;

        var start = position;
        var first = span[position];
        if (!(char.IsLetter(first) || first == '_'))
            return null;

        position++;
        while (position < span.Length)
        {
            var current = span[position];
            if (!(char.IsLetterOrDigit(current) || current is '_' or '-'))
                break;

            position++;
        }

        return span[start..position].ToString();
    }

    private static JsonElement EvaluatePropertySegment(
        JsonElement current,
        string propertyName,
        string currentStepId,
        string expression)
    {
        if (current.ValueKind == JsonValueKind.Object)
        {
            if (!current.TryGetProperty(propertyName, out var property))
                throw new InvalidOperationException($"Step '{currentStepId}': ref '{expression}' - field '{propertyName}' was not found.");

            return property.Clone();
        }

        if (current.ValueKind == JsonValueKind.Array)
            return ProjectArrayField(current, propertyName, currentStepId, expression);

        throw new InvalidOperationException(
            $"Step '{currentStepId}': ref '{expression}' - cannot access field '{propertyName}' on non-object.");
    }

    private static JsonElement EvaluateArrayAnySegment(
        JsonElement current,
        string currentStepId,
        string expression)
    {
        if (current.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Step '{currentStepId}': ref '{expression}' - value is not an array.");

        return current.Clone();
    }

    private static JsonElement EvaluateArrayIndexSegment(
        JsonElement current,
        int index,
        string currentStepId,
        string expression)
    {
        if (current.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Step '{currentStepId}': ref '{expression}' - value is not an array.");

        var items = current.EnumerateArray().ToArray();
        if (index < 0 || index >= items.Length)
            throw new InvalidOperationException($"Step '{currentStepId}': ref '{expression}' - index {index} out of range (count={items.Length}).");

        return items[index].Clone();
    }

    private static JsonElement ProjectArrayField(
        JsonElement array,
        string fieldName,
        string currentStepId,
        string expression)
    {
        var projected = new List<JsonElement>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Step '{currentStepId}': ref '{expression}' - cannot access field '{fieldName}' on non-object array item.");
            }

            if (!item.TryGetProperty(fieldName, out var property))
            {
                throw new InvalidOperationException(
                    $"Step '{currentStepId}': ref '{expression}' - field '{fieldName}' was not found on one of the array items.");
            }

            projected.Add(property.Clone());
        }

        return JsonSerializer.SerializeToElement(projected);
    }
}
