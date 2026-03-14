using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.PlanningRuntime.Verification;

public static class StepOutputContractValidator
{
    public static List<StepVerificationIssue> ValidateCallOutput(
        string stepId,
        ResolvedPlanStepOutputContract contract,
        JsonElement? output) =>
        Validate(stepId, contract.CallSchema, output, $"call output ({contract.Format}/{contract.Aggregate})");

    public static List<StepVerificationIssue> ValidateFinalOutput(
        string stepId,
        ResolvedPlanStepOutputContract contract,
        JsonElement? output) =>
        Validate(stepId, contract.FinalSchema, output, $"final output ({contract.Format}/{contract.Aggregate})");

    private static List<StepVerificationIssue> Validate(
        string stepId,
        JsonElement? schema,
        JsonElement? output,
        string scope)
    {
        var issues = new List<StepVerificationIssue>();
        if (schema is null || output is null)
            return issues;

        ValidateNode(stepId, output.Value, schema.Value, "$", scope, issues);
        return issues;
    }

    private static void ValidateNode(
        string stepId,
        JsonElement value,
        JsonElement schema,
        string path,
        string scope,
        List<StepVerificationIssue> issues)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            issues.Add(CreateIssue(stepId, "invalid_contract_schema", $"{scope}: schema at '{path}' is not an object."));
            return;
        }

        if (schema.TryGetProperty("enum", out var enumElement)
            && enumElement.ValueKind == JsonValueKind.Array
            && enumElement.EnumerateArray().Any()
            && !enumElement.EnumerateArray().Any(candidate => candidate.GetRawText() == value.GetRawText()))
        {
            issues.Add(CreateIssue(
                stepId,
                "output_contract_enum_mismatch",
                $"{scope}: value at '{path}' is not one of the allowed enum values."));
        }

        if (PlanStepOutputContractResolver.TryGetSchemaTypes(schema, out var expectedTypes)
            && !MatchesType(value, expectedTypes))
        {
            issues.Add(CreateIssue(
                stepId,
                "output_contract_type_mismatch",
                $"{scope}: expected {DescribeExpectedTypes(expectedTypes)} at '{path}', but got {DescribeKind(value)}."));
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
            ValidateObject(stepId, value, schema, path, scope, issues);

        if (value.ValueKind == JsonValueKind.Array)
            ValidateArray(stepId, value, schema, path, scope, issues);
    }

    private static void ValidateObject(
        string stepId,
        JsonElement value,
        JsonElement schema,
        string path,
        string scope,
        List<StepVerificationIssue> issues)
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
                    issues.Add(CreateIssue(
                        stepId,
                        "output_contract_missing_required",
                        $"{scope}: required property '{propertyName}' is missing at '{path}'."));
                }
            }
        }

        if (!schema.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in propertiesElement.EnumerateObject())
        {
            if (!value.TryGetProperty(property.Name, out var propertyValue))
                continue;

            ValidateNode(stepId, propertyValue, property.Value, $"{path}.{property.Name}", scope, issues);
        }
    }

    private static void ValidateArray(
        string stepId,
        JsonElement value,
        JsonElement schema,
        string path,
        string scope,
        List<StepVerificationIssue> issues)
    {
        if (!schema.TryGetProperty("items", out var itemsElement))
            return;

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            ValidateNode(stepId, item, itemsElement, $"{path}[{index}]", scope, issues);
            index++;
        }
    }

    private static StepVerificationIssue CreateIssue(string stepId, string code, string message) =>
        new()
        {
            Code = code,
            Message = $"Step '{stepId}': {message}"
        };

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
