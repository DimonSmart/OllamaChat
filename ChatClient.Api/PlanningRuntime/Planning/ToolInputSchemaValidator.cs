using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed record ToolInputSchemaIssue(
    string Code,
    string Message);

public static class ToolInputSchemaValidator
{
    public static IReadOnlyList<ToolInputSchemaIssue> ValidateResolvedInput(
        JsonElement input,
        JsonElement schema) =>
        ValidateNode(input, schema, "$");

    public static IReadOnlyList<ToolInputSchemaIssue> ValidateLiteralInput(
        JsonNode? value,
        JsonElement schema)
    {
        var element = value is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(value);

        return ValidateNode(element, schema, "$");
    }

    private static IReadOnlyList<ToolInputSchemaIssue> ValidateNode(
        JsonElement value,
        JsonElement schema,
        string path)
    {
        var issues = new List<ToolInputSchemaIssue>();

        if (schema.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ToolInputSchemaIssue(
                "invalid_input_schema",
                $"Input schema at '{path}' is not an object."));
            return issues;
        }

        if (schema.TryGetProperty("enum", out var enumElement)
            && enumElement.ValueKind == JsonValueKind.Array
            && enumElement.EnumerateArray().Any()
            && !enumElement.EnumerateArray().Any(candidate => candidate.GetRawText() == value.GetRawText()))
        {
            issues.Add(new ToolInputSchemaIssue(
                "input_contract_enum_mismatch",
                $"Value at '{path}' is not one of the allowed enum values."));
        }

        if (PlanStepOutputContractResolver.TryGetSchemaTypes(schema, out var expectedTypes)
            && !MatchesType(value, expectedTypes))
        {
            issues.Add(new ToolInputSchemaIssue(
                "input_contract_type_mismatch",
                $"Expected {DescribeExpectedTypes(expectedTypes)} at '{path}', but got {DescribeKind(value)}."));
            return issues;
        }

        if (value.ValueKind == JsonValueKind.Object)
            ValidateObject(value, schema, path, issues);

        if (value.ValueKind == JsonValueKind.Array)
            ValidateArray(value, schema, path, issues);

        return issues;
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        List<ToolInputSchemaIssue> issues)
    {
        if (schema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var requiredProperty in requiredElement.EnumerateArray())
            {
                var propertyName = requiredProperty.GetString();
                if (string.IsNullOrWhiteSpace(propertyName))
                    continue;

                if (!value.TryGetProperty(propertyName, out _))
                {
                    issues.Add(new ToolInputSchemaIssue(
                        "input_contract_missing_required",
                        $"Required property '{propertyName}' is missing at '{path}'."));
                }
            }
        }

        if (!schema.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!propertiesElement.TryGetProperty(property.Name, out var propertySchema))
                continue;

            issues.AddRange(ValidateNode(property.Value, propertySchema, $"{path}.{property.Name}"));
        }
    }

    private static void ValidateArray(
        JsonElement value,
        JsonElement schema,
        string path,
        List<ToolInputSchemaIssue> issues)
    {
        if (!schema.TryGetProperty("items", out var itemsElement))
            return;

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            issues.AddRange(ValidateNode(item, itemsElement, $"{path}[{index}]"));
            index++;
        }
    }

    private static bool MatchesType(JsonElement value, IReadOnlyList<string> expectedTypes) =>
        expectedTypes.Count == 0 || expectedTypes.Any(expectedType => MatchesType(value, expectedType));

    private static bool MatchesType(JsonElement value, string expectedType) =>
        expectedType switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
            _ => true
        };

    private static string DescribeExpectedTypes(IReadOnlyList<string> expectedTypes) =>
        string.Join("|", expectedTypes);

    private static string DescribeKind(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => "boolean",
            JsonValueKind.False => "boolean",
            JsonValueKind.Number when value.TryGetInt64(out _) => "integer",
            JsonValueKind.Number => "number",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "null",
            _ => value.ValueKind.ToString().ToLowerInvariant()
        };
}
