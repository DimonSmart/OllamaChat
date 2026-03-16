using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Planning;

public enum StepInputTypeKind
{
    String,
    Number,
    Integer,
    Boolean,
    Object,
    Array
}

public sealed record StepInputTypeSpec(
    StepInputTypeKind Kind,
    StepInputTypeSpec? ItemType = null)
{
    public override string ToString() =>
        Kind == StepInputTypeKind.Array && ItemType is not null
            ? $"array<{ItemType}>"
            : Kind switch
            {
                StepInputTypeKind.String => "string",
                StepInputTypeKind.Number => "number",
                StepInputTypeKind.Integer => "integer",
                StepInputTypeKind.Boolean => "boolean",
                StepInputTypeKind.Object => "object",
                StepInputTypeKind.Array => "array",
                _ => Kind.ToString().ToLowerInvariant()
            };
}

public sealed record StepInputTypeIssue(
    string Code,
    string Message);

public static class StepInputTypeValidator
{
    public static bool TryParse(string? text, out StepInputTypeSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Input type must be a non-empty string.";
            return false;
        }

        var normalized = text.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "string":
                spec = new StepInputTypeSpec(StepInputTypeKind.String);
                return true;
            case "number":
                spec = new StepInputTypeSpec(StepInputTypeKind.Number);
                return true;
            case "integer":
                spec = new StepInputTypeSpec(StepInputTypeKind.Integer);
                return true;
            case "boolean":
                spec = new StepInputTypeSpec(StepInputTypeKind.Boolean);
                return true;
            case "object":
                spec = new StepInputTypeSpec(StepInputTypeKind.Object);
                return true;
            case "array":
                spec = new StepInputTypeSpec(StepInputTypeKind.Array);
                return true;
        }

        if (!normalized.StartsWith("array<", StringComparison.Ordinal) || !normalized.EndsWith('>'))
        {
            error = $"Unsupported input type '{text}'. Supported types: string, number, integer, boolean, object, array, array<string>, array<number>, array<integer>, array<boolean>, array<object>.";
            return false;
        }

        var innerText = normalized["array<".Length..^1].Trim();
        if (innerText.Contains('<', StringComparison.Ordinal) || innerText.Contains('>', StringComparison.Ordinal))
        {
            error = $"Nested input type '{text}' is not supported.";
            return false;
        }

        if (!TryParse(innerText, out var itemType, out var itemError) || itemType is null)
        {
            error = itemError ?? $"Unsupported array item type '{innerText}'.";
            return false;
        }

        if (itemType.Kind == StepInputTypeKind.Array)
        {
            error = $"Nested array input type '{text}' is not supported.";
            return false;
        }

        spec = new StepInputTypeSpec(StepInputTypeKind.Array, itemType);
        return true;
    }

    public static IReadOnlyList<StepInputTypeIssue> ValidateResolvedValue(
        JsonElement? value,
        StepInputTypeSpec expectedType,
        string inputName)
    {
        var issues = new List<StepInputTypeIssue>();
        ValidateValue(value, expectedType, inputName, issues);
        return issues;
    }

    public static IReadOnlyList<StepInputTypeIssue> ValidateSourceSchema(
        JsonElement sourceSchema,
        StepInputTypeSpec expectedType,
        string inputName)
    {
        var issues = new List<StepInputTypeIssue>();
        ValidateSchema(sourceSchema, expectedType, inputName, issues);
        return issues;
    }

    private static void ValidateValue(
        JsonElement? value,
        StepInputTypeSpec expectedType,
        string path,
        List<StepInputTypeIssue> issues)
    {
        if (value is null)
        {
            issues.Add(new StepInputTypeIssue(
                "llm_input_type_null",
                $"Input '{path}' resolved to null, but declared type '{expectedType}' does not allow null."));
            return;
        }

        var element = value.Value;
        switch (expectedType.Kind)
        {
            case StepInputTypeKind.String:
                if (element.ValueKind != JsonValueKind.String)
                    AddValueMismatch(path, expectedType, element, issues);
                return;
            case StepInputTypeKind.Number:
                if (element.ValueKind != JsonValueKind.Number)
                    AddValueMismatch(path, expectedType, element, issues);
                return;
            case StepInputTypeKind.Integer:
                if (element.ValueKind != JsonValueKind.Number || !IsInteger(element))
                    AddValueMismatch(path, expectedType, element, issues);
                return;
            case StepInputTypeKind.Boolean:
                if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    AddValueMismatch(path, expectedType, element, issues);
                return;
            case StepInputTypeKind.Object:
                if (element.ValueKind != JsonValueKind.Object)
                    AddValueMismatch(path, expectedType, element, issues);
                return;
            case StepInputTypeKind.Array:
                if (element.ValueKind != JsonValueKind.Array)
                {
                    AddValueMismatch(path, expectedType, element, issues);
                    return;
                }

                if (expectedType.ItemType is null)
                    return;

                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ValidateValue(item, expectedType.ItemType, $"{path}[{index}]", issues);
                    index++;
                }

                return;
            default:
                issues.Add(new StepInputTypeIssue(
                    "llm_input_type_unsupported",
                    $"Input '{path}' uses unsupported declared type '{expectedType}'."));
                return;
        }
    }

    private static void ValidateSchema(
        JsonElement sourceSchema,
        StepInputTypeSpec expectedType,
        string inputName,
        List<StepInputTypeIssue> issues)
    {
        if (!PlanStepOutputContractResolver.TryGetSchemaTypes(sourceSchema, out var sourceTypes))
            return;

        if (sourceTypes.Contains("null", StringComparer.Ordinal))
        {
            issues.Add(new StepInputTypeIssue(
                "llm_input_type_nullable",
                $"Input '{inputName}' may resolve to null, but declared type '{expectedType}' does not allow null."));
            return;
        }

        var incompatibleTypes = sourceTypes
            .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
            .Where(type => !IsSchemaTypeAccepted(expectedType, type))
            .ToList();
        if (incompatibleTypes.Count > 0)
        {
            issues.Add(new StepInputTypeIssue(
                "llm_input_type_mismatch",
                $"Input '{inputName}' declares type '{expectedType}', but the bound source schema produces {string.Join("|", incompatibleTypes)}."));
            return;
        }

        if (expectedType.Kind != StepInputTypeKind.Array
            || expectedType.ItemType is null
            || !sourceSchema.TryGetProperty("items", out var itemSchema))
        {
            return;
        }

        ValidateSchema(itemSchema, expectedType.ItemType, $"{inputName}[]", issues);
    }

    private static bool IsSchemaTypeAccepted(StepInputTypeSpec expectedType, string sourceType) =>
        expectedType.Kind switch
        {
            StepInputTypeKind.String => string.Equals(sourceType, "string", StringComparison.Ordinal),
            StepInputTypeKind.Number => string.Equals(sourceType, "number", StringComparison.Ordinal)
                || string.Equals(sourceType, "integer", StringComparison.Ordinal),
            StepInputTypeKind.Integer => string.Equals(sourceType, "integer", StringComparison.Ordinal),
            StepInputTypeKind.Boolean => string.Equals(sourceType, "boolean", StringComparison.Ordinal),
            StepInputTypeKind.Object => string.Equals(sourceType, "object", StringComparison.Ordinal),
            StepInputTypeKind.Array => string.Equals(sourceType, "array", StringComparison.Ordinal),
            _ => false
        };

    private static void AddValueMismatch(
        string path,
        StepInputTypeSpec expectedType,
        JsonElement actual,
        List<StepInputTypeIssue> issues) =>
        issues.Add(new StepInputTypeIssue(
            "llm_input_type_mismatch",
            $"Input '{path}' expected '{expectedType}', but got {DescribeValue(actual)}."));

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _))
            return true;

        if (value.TryGetDecimal(out var decimalValue))
            return decimal.Truncate(decimalValue) == decimalValue;

        return false;
    }

    private static string DescribeValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number when IsInteger(value) => "integer",
            JsonValueKind.Number => "number",
            JsonValueKind.String => "string",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.Null => "null",
            _ => value.ValueKind.ToString().ToLowerInvariant()
        };
}
