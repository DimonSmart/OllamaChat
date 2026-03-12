using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.PlanningRuntime.Verification;

public static class StepOutputVerifier
{
    public static List<StepVerificationIssue> Verify(PlanStep step, JsonElement? output)
    {
        var issues = new List<StepVerificationIssue>();

        if (output is null || output.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            issues.Add(new StepVerificationIssue
            {
                Code = "null_output",
                Message = $"Step '{step.Id}' completed successfully but produced null output."
            });
            return issues;
        }

        switch (output.Value.ValueKind)
        {
            case JsonValueKind.String when string.IsNullOrWhiteSpace(output.Value.GetString()):
                issues.Add(new StepVerificationIssue
                {
                    Code = "empty_string_output",
                    Message = $"Step '{step.Id}' produced an empty string output."
                });
                return issues;

            case JsonValueKind.Object when !output.Value.EnumerateObject().Any():
                issues.Add(new StepVerificationIssue
                {
                    Code = "empty_object_output",
                    Message = $"Step '{step.Id}' produced an empty object output."
                });
                return issues;

            case JsonValueKind.Array when !output.Value.EnumerateArray().Any():
                issues.Add(new StepVerificationIssue
                {
                    Code = "empty_array_output",
                    Message = $"Step '{step.Id}' produced an empty array output."
                });
                return issues;
        }

        if (output.Value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in output.Value.EnumerateArray())
            {
                if (!IsStructurallyEmpty(item))
                {
                    index++;
                    continue;
                }

                issues.Add(new StepVerificationIssue
                {
                    Code = "structurally_empty_array_item",
                    Message = $"Step '{step.Id}' produced a structurally empty item at index {index}."
                });

                index++;
            }

            return issues;
        }

        if (IsStructurallyEmpty(output.Value))
        {
            issues.Add(new StepVerificationIssue
            {
                Code = "structurally_empty_output",
                Message = $"Step '{step.Id}' produced a structurally empty output."
            });
        }

        return issues;
    }

    private static bool IsStructurallyEmpty(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;

            case JsonValueKind.String:
                return string.IsNullOrWhiteSpace(node.GetString());

            case JsonValueKind.Object:
                if (!node.EnumerateObject().Any())
                    return true;

                return node.EnumerateObject().All(property => IsStructurallyEmpty(property.Value));

            case JsonValueKind.Array:
                if (!node.EnumerateArray().Any())
                    return true;

                return node.EnumerateArray().All(IsStructurallyEmpty);

            default:
                return false;
        }
    }
}

