using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class PlanEditingSession
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlanDefinition _plan;

    public PlanEditingSession(PlanDefinition sourcePlan)
    {
        _plan = sourcePlan;
    }

    public JsonObject GetCurrentPlanJson() =>
        JsonSerializer.SerializeToNode(_plan, JsonOptions)?.AsObject()
        ?? throw new InvalidOperationException("Failed to serialize the working plan.");

    public PlanDefinition BuildPlan() => _plan;

    public JsonObject ExecuteAction(string toolName, JsonObject input)
    {
        try
        {
            return toolName switch
            {
                "plan.readStep" => CreateSuccess(toolName, ReadStep(GetRequiredString(input, "stepId"))),
                "plan.replaceStep" => CreateSuccess(toolName, ReplaceStep(GetRequiredString(input, "stepId"), input["step"])),
                "plan.addSteps" => CreateSuccess(toolName, AddSteps(GetOptionalString(input, "afterStepId"), input["steps"])),
                "plan.resetFrom" => CreateSuccess(toolName, ResetFrom(GetRequiredString(input, "stepId"))),
                _ => CreateFailure("unknown_tool", $"Unknown replanning tool '{toolName ?? "<null>"}'.", toolName)
            };
        }
        catch (Exception ex)
        {
            return CreateFailure("tool_error", ex.Message, toolName);
        }
    }

    private JsonNode? ReadStep(string stepId)
    {
        var step = _plan.Steps.FirstOrDefault(candidate => string.Equals(candidate.Id, stepId, StringComparison.Ordinal));
        if (step is null)
            throw new InvalidOperationException($"Step '{stepId}' was not found.");

        return JsonSerializer.SerializeToNode(step, JsonOptions);
    }

    private JsonNode? ReplaceStep(string stepId, JsonNode? stepNode)
    {
        var stepIndex = FindStepIndex(stepId);
        var replacementStep = DeserializeStep(stepNode);

        EnsureUniqueStepId(replacementStep.Id, stepIndex);
        _plan.Steps[stepIndex] = replacementStep;
        PlanExecutionState.ResetFrom(_plan, stepIndex);

        return new JsonObject
        {
            ["stepId"] = replacementStep.Id,
            ["position"] = stepIndex
        };
    }

    private JsonNode? AddSteps(string? afterStepId, JsonNode? stepsNode)
    {
        var newSteps = DeserializeSteps(stepsNode);
        var newStepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var newStep in newSteps)
        {
            if (!newStepIds.Add(newStep.Id))
                throw new InvalidOperationException($"Step id '{newStep.Id}' is duplicated inside the inserted step list.");

            EnsureUniqueStepId(newStep.Id, excludedIndex: null);
        }

        if (afterStepId is null && _plan.Steps.Count > 0)
            throw new InvalidOperationException("Action input 'afterStepId' is required unless the working plan is empty.");

        var insertIndex = afterStepId is null
            ? 0
            : FindStepIndex(afterStepId) + 1;

        _plan.Steps.InsertRange(insertIndex, newSteps);
        PlanExecutionState.ResetFrom(_plan, insertIndex);

        return new JsonObject
        {
            ["insertedCount"] = newSteps.Count,
            ["insertIndex"] = insertIndex
        };
    }

    private JsonNode? ResetFrom(string stepId)
    {
        var startIndex = FindStepIndex(stepId);
        var resetSteps = _plan.Steps.Skip(startIndex).Select(step => step.Id).ToList();
        PlanExecutionState.ResetFrom(_plan, startIndex);

        return new JsonObject
        {
            ["resetCount"] = resetSteps.Count,
            ["resetStepIds"] = new JsonArray(resetSteps.Select(resetStepId => JsonValue.Create(resetStepId)).ToArray())
        };
    }

    private int FindStepIndex(string stepId)
    {
        var stepIndex = _plan.Steps.FindIndex(step => string.Equals(step.Id, stepId, StringComparison.Ordinal));
        if (stepIndex < 0)
            throw new InvalidOperationException($"Step '{stepId}' was not found.");

        return stepIndex;
    }

    private void EnsureUniqueStepId(string stepId, int? excludedIndex)
    {
        var duplicateIndex = _plan.Steps.FindIndex(step => string.Equals(step.Id, stepId, StringComparison.Ordinal));
        if (duplicateIndex >= 0 && duplicateIndex != excludedIndex)
            throw new InvalidOperationException($"Step id '{stepId}' already exists in the working plan.");
    }

    private static string GetRequiredString(JsonObject input, string propertyName)
    {
        var value = GetOptionalString(input, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException($"Action input '{propertyName}' is required.");
    }

    private static string? GetOptionalString(JsonObject input, string propertyName) =>
        input[propertyName]?.GetValue<string>()?.Trim();

    private static PlanStep DeserializeStep(JsonNode? stepNode) =>
        stepNode?.Deserialize<PlanStep>(JsonOptions)
        ?? throw new InvalidOperationException("Action input 'step' must be a valid plan step object.");

    private static IReadOnlyList<PlanStep> DeserializeSteps(JsonNode? stepsNode)
    {
        var steps = stepsNode?.Deserialize<List<PlanStep>>(JsonOptions);
        if (steps is not { Count: > 0 })
            throw new InvalidOperationException("Action input 'steps' must contain at least one plan step.");

        return steps;
    }

    private static JsonObject CreateSuccess(string? toolName, JsonNode? output) => new()
    {
        ["tool"] = toolName,
        ["ok"] = true,
        ["output"] = output?.DeepClone()
    };

    private static JsonObject CreateFailure(string code, string message, string? toolName = null) => new()
    {
        ["tool"] = toolName,
        ["ok"] = false,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };
}

