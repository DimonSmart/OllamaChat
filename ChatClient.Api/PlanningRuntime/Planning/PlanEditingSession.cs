using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Common;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class PlanEditingSession
{
    private readonly PlanDefinition _plan;
    private readonly PlanModelProfile _profile;

    public PlanEditingSession(PlanDefinition sourcePlan, PlanModelProfile profile = PlanModelProfile.Runtime)
    {
        _plan = sourcePlan;
        _profile = profile;
        PlanSanitizer.Sanitize(_plan, _profile);
    }

    public PlanModelProfile Profile => _profile;

    public JsonObject GetCurrentPlanJson() =>
        PlanJsonProfiles.SerializeToNode(_plan, _profile)?.AsObject()
        ?? throw new InvalidOperationException("Failed to serialize the working plan.");

    public PlanDefinition BuildPlan()
    {
        PlanSanitizer.Sanitize(_plan, _profile);
        return _plan;
    }

    public JsonObject ExecuteAction(string toolName, JsonObject input)
    {
        try
        {
            return toolName switch
            {
                "plan.readStep" => CreateSuccess(toolName, ReadStep(GetRequiredString(input, "stepId"))),
                "plan.replaceStep" => CreateSuccess(toolName, ReplaceStep(GetReplaceStepId(input), GetReplaceStepNode(input))),
                "plan.addSteps" => CreateSuccess(toolName, AddSteps(GetOptionalString(input, "afterStepId"), input["steps"])),
                "plan.removeStep" => CreateSuccess(toolName, RemoveStep(GetRequiredString(input, "stepId"))),
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

        return PlanJsonProfiles.SerializeToNode(step, _profile);
    }

    private JsonNode? ReplaceStep(string stepId, JsonNode? stepNode)
    {
        var stepIndex = FindStepIndex(stepId);
        var existingStep = _plan.Steps[stepIndex];
        var replacementStep = DeserializeStep(stepNode);

        EnsureUniqueStepId(replacementStep.Id, stepIndex);
        _plan.Steps[stepIndex] = replacementStep;
        PlanExecutionState.ResetFrom(_plan, stepIndex);

        return new JsonObject
        {
            ["stepId"] = replacementStep.Id,
            ["position"] = stepIndex,
            ["before"] = PlanningLogFormatter.SummarizeStep(existingStep, _profile),
            ["after"] = PlanningLogFormatter.SummarizeStep(replacementStep, _profile),
            ["diff"] = BuildStepDiff(existingStep, replacementStep)
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
            ["insertIndex"] = insertIndex,
            ["insertedSteps"] = new JsonArray(newSteps.Select(step => PlanningLogFormatter.SummarizeStep(step, _profile)).ToArray())
        };
    }

    private JsonNode? RemoveStep(string stepId)
    {
        var stepIndex = FindStepIndex(stepId);
        var removedStep = _plan.Steps[stepIndex];
        _plan.Steps.RemoveAt(stepIndex);

        var resetStepIds = new List<string>();
        if (stepIndex < _plan.Steps.Count)
        {
            resetStepIds = _plan.Steps.Skip(stepIndex).Select(step => step.Id).ToList();
            PlanExecutionState.ResetFrom(_plan, stepIndex);
        }

        return new JsonObject
        {
            ["stepId"] = stepId,
            ["position"] = stepIndex,
            ["removed"] = PlanningLogFormatter.SummarizeStep(removedStep, _profile),
            ["resetCount"] = resetStepIds.Count,
            ["resetStepIds"] = new JsonArray(resetStepIds.Select(resetStepId => JsonValue.Create(resetStepId)).ToArray())
        };
    }

    private JsonNode? ResetFrom(string stepId)
    {
        var startIndex = FindStepIndex(stepId);
        var resetSteps = _plan.Steps.Skip(startIndex).Select(step => step.Id).ToList();
        PlanExecutionState.ResetFrom(_plan, startIndex);

        return new JsonObject
        {
            ["fromStepId"] = stepId,
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

    private static string GetReplaceStepId(JsonObject input)
    {
        return GetRequiredString(input, "stepId");
    }

    private static JsonNode? GetReplaceStepNode(JsonObject input)
    {
        if (input.TryGetPropertyValue("step", out var stepNode) && stepNode is not null)
            return stepNode;

        throw new InvalidOperationException("Action input 'step' must be a valid plan step object.");
    }

    private PlanStep DeserializeStep(JsonNode? stepNode) =>
        SanitizeStep(
            PlanJsonProfiles.Deserialize<PlanStep>(stepNode, _profile)
            ?? throw new InvalidOperationException("Action input 'step' must be a valid plan step object."));

    private IReadOnlyList<PlanStep> DeserializeSteps(JsonNode? stepsNode)
    {
        var steps = PlanJsonProfiles.Deserialize<List<PlanStep>>(stepsNode, _profile);
        if (steps is not { Count: > 0 })
            throw new InvalidOperationException("Action input 'steps' must contain at least one plan step.");

        foreach (var step in steps)
            SanitizeStep(step);

        return steps;
    }

    private PlanStep SanitizeStep(PlanStep step)
    {
        if (_profile == PlanModelProfile.Draft)
            PlanExecutionState.ResetStep(step);

        return step;
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

    private JsonObject BuildStepDiff(PlanStep before, PlanStep after)
    {
        var diff = new JsonObject();

        if (!string.Equals(before.Kind, after.Kind, StringComparison.Ordinal)
            || !string.Equals(before.CapabilityId, after.CapabilityId, StringComparison.Ordinal))
        {
            diff["capability"] = new JsonObject
            {
                ["before"] = $"{PlanStepKinds.GetKind(before)}:{PlanStepKinds.GetCapabilityId(before)}",
                ["after"] = $"{PlanStepKinds.GetKind(after)}:{PlanStepKinds.GetCapabilityId(after)}"
            };
        }

        if (!string.Equals(before.SystemPrompt, after.SystemPrompt, StringComparison.Ordinal))
        {
            diff["systemPrompt"] = new JsonObject
            {
                ["before"] = PlanningLogFormatter.SummarizeText(before.SystemPrompt, 120),
                ["after"] = PlanningLogFormatter.SummarizeText(after.SystemPrompt, 120)
            };
        }

        if (!string.Equals(before.UserPrompt, after.UserPrompt, StringComparison.Ordinal))
        {
            diff["userPrompt"] = new JsonObject
            {
                ["before"] = PlanningLogFormatter.SummarizeText(before.UserPrompt, 120),
                ["after"] = PlanningLogFormatter.SummarizeText(after.UserPrompt, 120)
            };
        }

        var beforeOut = PlanJsonProfiles.SerializeToNode(before.Out, _profile);
        var afterOut = PlanJsonProfiles.SerializeToNode(after.Out, _profile);
        if (!NodesEqual(beforeOut, afterOut))
        {
            diff["output"] = new JsonObject
            {
                ["before"] = PlanningLogFormatter.SummarizeNodeValue(beforeOut, maxDepth: 3),
                ["after"] = PlanningLogFormatter.SummarizeNodeValue(afterOut, maxDepth: 3)
            };
        }

        var beforeKeys = before.In.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray();
        var afterKeys = after.In.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray();
        if (!beforeKeys.SequenceEqual(afterKeys, StringComparer.Ordinal))
        {
            diff["inputKeys"] = new JsonObject
            {
                ["before"] = new JsonArray(beforeKeys.Select(static key => JsonValue.Create(key)).ToArray()),
                ["after"] = new JsonArray(afterKeys.Select(static key => JsonValue.Create(key)).ToArray())
            };
        }

        var changedInputs = new JsonArray();
        foreach (var key in before.In.Keys.Concat(after.In.Keys).Distinct(StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal))
        {
            before.In.TryGetValue(key, out var beforeValue);
            after.In.TryGetValue(key, out var afterValue);
            if (NodesEqual(beforeValue, afterValue))
                continue;

            changedInputs.Add(new JsonObject
            {
                ["name"] = key,
                ["before"] = PlanningLogFormatter.SummarizeNodeValue(beforeValue, maxDepth: 3),
                ["after"] = PlanningLogFormatter.SummarizeNodeValue(afterValue, maxDepth: 3)
            });
        }

        if (changedInputs.Count > 0)
            diff["changedInputs"] = changedInputs;

        return diff;
    }

    private static bool NodesEqual(JsonNode? left, JsonNode? right) =>
        string.Equals(
            left?.ToJsonString(PlanningJson.CompactOptions),
            right?.ToJsonString(PlanningJson.CompactOptions),
            StringComparison.Ordinal);
}
