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
        ValidateResolvedNode(input, schema, "$");

    public static IReadOnlyList<ToolInputSchemaIssue> ValidateLiteralInput(
        JsonNode? value,
        JsonElement schema)
    {
        var element = value is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(value);

        return ValidateResolvedNode(element, schema, "$");
    }

    public static IReadOnlyList<ToolInputSchemaIssue> ValidateDraftInput(
        JsonNode? value,
        JsonElement schema) =>
        ValidateDraftNode(value, schema, "$");

    private static IReadOnlyList<ToolInputSchemaIssue> ValidateResolvedNode(
        JsonElement value,
        JsonElement schema,
        string path)
    {
        var compositeIssues = ValidateResolvedComposite(value, schema, path);
        if (compositeIssues is not null)
            return compositeIssues;

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
            ValidateResolvedObject(value, schema, path, issues);

        if (value.ValueKind == JsonValueKind.Array)
            ValidateResolvedArray(value, schema, path, issues);

        return issues;
    }

    private static IReadOnlyList<ToolInputSchemaIssue> ValidateDraftNode(
        JsonNode? value,
        JsonElement schema,
        string path)
    {
        if (PlanInputBindingSyntax.TryParseBinding(value, out _, out _))
            return [];

        var compositeIssues = ValidateDraftComposite(value, schema, path);
        if (compositeIssues is not null)
            return compositeIssues;

        var issues = new List<ToolInputSchemaIssue>();
        if (schema.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ToolInputSchemaIssue(
                "invalid_input_schema",
                $"Input schema at '{path}' is not an object."));
            return issues;
        }

        var element = SerializeNode(value);

        if (schema.TryGetProperty("enum", out var enumElement)
            && enumElement.ValueKind == JsonValueKind.Array
            && enumElement.EnumerateArray().Any()
            && !enumElement.EnumerateArray().Any(candidate => candidate.GetRawText() == element.GetRawText()))
        {
            issues.Add(new ToolInputSchemaIssue(
                "input_contract_enum_mismatch",
                $"Value at '{path}' is not one of the allowed enum values."));
        }

        if (PlanStepOutputContractResolver.TryGetSchemaTypes(schema, out var expectedTypes)
            && !MatchesType(element, expectedTypes))
        {
            issues.Add(new ToolInputSchemaIssue(
                "input_contract_type_mismatch",
                $"Expected {DescribeExpectedTypes(expectedTypes)} at '{path}', but got {DescribeKind(element)}."));
            return issues;
        }

        if (value is JsonObject obj)
            ValidateDraftObject(obj, schema, path, issues);

        if (value is JsonArray array)
            ValidateDraftArray(array, schema, path, issues);

        return issues;
    }

    private static IReadOnlyList<ToolInputSchemaIssue>? ValidateResolvedComposite(
        JsonElement value,
        JsonElement schema,
        string path)
    {
        if (TryGetCompositeVariants(schema, "oneOf", out var oneOfVariants))
            return ValidateResolvedOneOf(value, oneOfVariants, path);

        if (TryGetCompositeVariants(schema, "anyOf", out var anyOfVariants))
            return ValidateResolvedAnyOf(value, anyOfVariants, path);

        return null;
    }

    private static IReadOnlyList<ToolInputSchemaIssue>? ValidateDraftComposite(
        JsonNode? value,
        JsonElement schema,
        string path)
    {
        if (TryGetCompositeVariants(schema, "oneOf", out var oneOfVariants))
            return ValidateDraftOneOf(value, oneOfVariants, path);

        if (TryGetCompositeVariants(schema, "anyOf", out var anyOfVariants))
            return ValidateDraftAnyOf(value, anyOfVariants, path);

        return null;
    }

    private static IReadOnlyList<ToolInputSchemaIssue> ValidateResolvedAnyOf(
        JsonElement value,
        IReadOnlyList<JsonElement> variants,
        string path)
    {
        List<ToolInputSchemaIssue>? bestIssues = null;
        foreach (var variant in variants)
        {
            var issues = ValidateResolvedNode(value, variant, path);
            if (issues.Count == 0)
                return [];

            if (bestIssues is null || issues.Count < bestIssues.Count)
                bestIssues = [.. issues];
        }

        var result = new List<ToolInputSchemaIssue>
        {
            new(
                "input_contract_anyof_mismatch",
                $"Value at '{path}' does not match any allowed schema alternative.")
        };

        if (bestIssues is not null)
            result.AddRange(bestIssues);

        return result;
    }

    private static IReadOnlyList<ToolInputSchemaIssue> ValidateDraftAnyOf(
        JsonNode? value,
        IReadOnlyList<JsonElement> variants,
        string path)
    {
        List<ToolInputSchemaIssue>? bestIssues = null;
        foreach (var variant in variants)
        {
            var issues = ValidateDraftNode(value, variant, path);
            if (issues.Count == 0)
                return [];

            if (bestIssues is null || issues.Count < bestIssues.Count)
                bestIssues = [.. issues];
        }

        var result = new List<ToolInputSchemaIssue>
        {
            new(
                "input_contract_anyof_mismatch",
                $"Value at '{path}' does not match any allowed schema alternative.")
        };

        if (bestIssues is not null)
            result.AddRange(bestIssues);

        return result;
    }

    private static IReadOnlyList<ToolInputSchemaIssue> ValidateResolvedOneOf(
        JsonElement value,
        IReadOnlyList<JsonElement> variants,
        string path)
    {
        var successCount = 0;
        List<ToolInputSchemaIssue>? bestIssues = null;
        foreach (var variant in variants)
        {
            var issues = ValidateResolvedNode(value, variant, path);
            if (issues.Count == 0)
            {
                successCount++;
                continue;
            }

            if (bestIssues is null || issues.Count < bestIssues.Count)
                bestIssues = [.. issues];
        }

        if (successCount == 1)
            return [];

        if (successCount > 1)
        {
            return
            [
                new ToolInputSchemaIssue(
                    "input_contract_oneof_multiple",
                    $"Value at '{path}' matches multiple oneOf schema alternatives; exactly one is required.")
            ];
        }

        var result = new List<ToolInputSchemaIssue>
        {
            new(
                "input_contract_oneof_mismatch",
                $"Value at '{path}' does not match any allowed schema alternative.")
        };

        if (bestIssues is not null)
            result.AddRange(bestIssues);

        return result;
    }

    private static IReadOnlyList<ToolInputSchemaIssue> ValidateDraftOneOf(
        JsonNode? value,
        IReadOnlyList<JsonElement> variants,
        string path)
    {
        var successCount = 0;
        List<ToolInputSchemaIssue>? bestIssues = null;
        foreach (var variant in variants)
        {
            var issues = ValidateDraftNode(value, variant, path);
            if (issues.Count == 0)
            {
                successCount++;
                continue;
            }

            if (bestIssues is null || issues.Count < bestIssues.Count)
                bestIssues = [.. issues];
        }

        if (successCount == 1)
            return [];

        if (successCount > 1)
        {
            return
            [
                new ToolInputSchemaIssue(
                    "input_contract_oneof_multiple",
                    $"Value at '{path}' matches multiple oneOf schema alternatives; exactly one is required.")
            ];
        }

        var result = new List<ToolInputSchemaIssue>
        {
            new(
                "input_contract_oneof_mismatch",
                $"Value at '{path}' does not match any allowed schema alternative.")
        };

        if (bestIssues is not null)
            result.AddRange(bestIssues);

        return result;
    }

    private static void ValidateResolvedObject(
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

            issues.AddRange(ValidateResolvedNode(property.Value, propertySchema, $"{path}.{property.Name}"));
        }
    }

    private static void ValidateDraftObject(
        JsonObject value,
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

                if (!value.ContainsKey(propertyName))
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

        foreach (var property in value)
        {
            if (property.Key is null || !propertiesElement.TryGetProperty(property.Key, out var propertySchema))
                continue;

            issues.AddRange(ValidateDraftNode(property.Value, propertySchema, $"{path}.{property.Key}"));
        }
    }

    private static void ValidateResolvedArray(
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
            issues.AddRange(ValidateResolvedNode(item, itemsElement, $"{path}[{index}]"));
            index++;
        }
    }

    private static void ValidateDraftArray(
        JsonArray value,
        JsonElement schema,
        string path,
        List<ToolInputSchemaIssue> issues)
    {
        if (!schema.TryGetProperty("items", out var itemsElement))
            return;

        for (var index = 0; index < value.Count; index++)
            issues.AddRange(ValidateDraftNode(value[index], itemsElement, $"{path}[{index}]"));
    }

    private static bool TryGetCompositeVariants(
        JsonElement schema,
        string propertyName,
        out IReadOnlyList<JsonElement> variants)
    {
        variants = Array.Empty<JsonElement>();
        if (!schema.TryGetProperty(propertyName, out var variantsElement)
            || variantsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        variants = variantsElement
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => item.Clone())
            .ToArray();

        return variants.Count > 0;
    }

    private static JsonElement SerializeNode(JsonNode? node) =>
        node is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(node);

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
