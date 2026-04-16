using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.LowLevel;

internal sealed class LowLevelEditingSession(OutlinePlan outlinePlan)
{
    private readonly OutlinePlan _outlinePlan = outlinePlan ?? throw new ArgumentNullException(nameof(outlinePlan));
    private string _goal = outlinePlan.Goal;
    private string? _blockedReason;
    private string? _resultStepId;
    private readonly string? _outlineResultNodeId = outlinePlan.ResultNodeId;
    private readonly List<LowLevelStep> _steps = [];

    public JsonObject GetCurrentPlanJson() =>
        PlanningNodeJson.ToNode(BuildPlan())?.AsObject()
        ?? new JsonObject();

    public LowLevelPlan BuildPlan() => new()
    {
        Goal = _goal,
        BlockedReason = NormalizeOptional(_blockedReason),
        OutlineResultNodeId = _outlineResultNodeId,
        ResultStepId = NormalizeOptional(_resultStepId),
        Steps = [.. _steps.Select(step => CloneStep(step, string.Equals(step.Id, _resultStepId, StringComparison.OrdinalIgnoreCase)))]
    };

    public JsonObject ExecuteAction(string toolName, JsonObject input)
    {
        try
        {
            return toolName switch
            {
                "low.readPlan" => CreateSuccess(toolName, GetCurrentPlanJson()),
                "low.readStep" => CreateSuccess(toolName, ReadStep(GetRequiredString(input, "stepId"))),
                "low.setGoal" => CreateSuccess(toolName, SetGoal(GetRequiredString(input, "goal"))),
                "low.setBlockedReason" => CreateSuccess(toolName, SetBlockedReason(GetOptionalString(input, "blockedReason"))),
                "low.addStep" => CreateSuccess(toolName, AddStep(GetOptionalString(input, "afterStepId"), input["step"])),
                "low.replaceStep" => CreateSuccess(toolName, ReplaceStep(GetRequiredString(input, "stepId"), input["step"])),
                "low.removeStep" => CreateSuccess(toolName, RemoveStep(GetRequiredString(input, "stepId"))),
                "low.rewireInput" => CreateSuccess(
                    toolName,
                    RewireInput(
                        GetRequiredString(input, "stepId"),
                        GetRequiredString(input, "inputName"),
                        input["source"])),
                "low.markResultStep" => CreateSuccess(toolName, MarkResultStep(GetRequiredString(input, "stepId"))),
                _ => CreateFailure("unknown_tool", $"Unknown low-level tool '{toolName ?? "<null>"}'.", toolName)
            };
        }
        catch (Exception ex)
        {
            return CreateFailure("tool_error", ex.Message, toolName);
        }
    }

    private JsonNode? ReadStep(string stepId)
    {
        var step = _steps.FirstOrDefault(candidate => string.Equals(candidate.Id, stepId, StringComparison.OrdinalIgnoreCase));
        if (step is null)
            throw new InvalidOperationException($"Low-level step '{stepId}' was not found.");

        return PlanningNodeJson.ToNode(CloneStep(step, string.Equals(step.Id, _resultStepId, StringComparison.OrdinalIgnoreCase)));
    }

    private JsonObject SetGoal(string goal)
    {
        _goal = goal.Trim();
        return new JsonObject
        {
            ["goal"] = _goal
        };
    }

    private JsonObject SetBlockedReason(string? blockedReason)
    {
        _blockedReason = NormalizeOptional(blockedReason);
        if (!string.IsNullOrWhiteSpace(_blockedReason))
            _resultStepId = null;

        return new JsonObject
        {
            ["blockedReason"] = _blockedReason is null ? null : JsonValue.Create(_blockedReason)
        };
    }

    private JsonObject AddStep(string? afterStepId, JsonNode? stepNode)
    {
        var step = DeserializeStep(stepNode);
        EnsureUniqueStepId(step.Id, excludedIndex: null);

        var insertIndex = string.IsNullOrWhiteSpace(afterStepId)
            ? _steps.Count
            : FindStepIndex(afterStepId) + 1;
        _steps.Insert(insertIndex, step);

        return new JsonObject
        {
            ["stepId"] = step.Id,
            ["position"] = insertIndex
        };
    }

    private JsonObject ReplaceStep(string stepId, JsonNode? stepNode)
    {
        var index = FindStepIndex(stepId);
        var step = DeserializeStep(stepNode);
        EnsureUniqueStepId(step.Id, index);
        _steps[index] = step;

        if (string.Equals(_resultStepId, stepId, StringComparison.OrdinalIgnoreCase))
            _resultStepId = step.Id;

        foreach (var candidate in _steps)
        {
            foreach (var input in candidate.Inputs.Where(input => string.Equals(input.Source.StepId, stepId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                candidate.Inputs.Remove(input);
                candidate.Inputs.Add(new LowLevelStepInput
                {
                    Name = input.Name,
                    Source = new LowLevelInputSource
                    {
                        Kind = input.Source.Kind,
                        Value = PlanningNodeJson.CloneNode(input.Source.Value),
                        StepId = step.Id,
                        Port = input.Source.Port,
                        Mode = input.Source.Mode
                    }
                });
            }
        }

        return new JsonObject
        {
            ["stepId"] = step.Id,
            ["position"] = index
        };
    }

    private JsonObject RemoveStep(string stepId)
    {
        var index = FindStepIndex(stepId);
        _steps.RemoveAt(index);

        foreach (var step in _steps)
            step.Inputs.RemoveAll(input => string.Equals(input.Source.StepId, stepId, StringComparison.OrdinalIgnoreCase));

        if (string.Equals(_resultStepId, stepId, StringComparison.OrdinalIgnoreCase))
            _resultStepId = null;

        return new JsonObject
        {
            ["stepId"] = stepId,
            ["remainingCount"] = _steps.Count
        };
    }

    private JsonObject RewireInput(string stepId, string inputName, JsonNode? sourceNode)
    {
        var step = _steps[FindStepIndex(stepId)];
        var source = DeserializeSource(sourceNode);
        step.Inputs.RemoveAll(input => string.Equals(input.Name, inputName, StringComparison.OrdinalIgnoreCase));
        step.Inputs.Add(new LowLevelStepInput
        {
            Name = inputName,
            Source = source
        });

        return new JsonObject
        {
            ["stepId"] = stepId,
            ["inputName"] = inputName
        };
    }

    private JsonObject MarkResultStep(string stepId)
    {
        FindStepIndex(stepId);
        _resultStepId = stepId;
        _blockedReason = null;

        return new JsonObject
        {
            ["resultStepId"] = _resultStepId
        };
    }

    public JsonObject Validate(IReadOnlyCollection<AppToolDescriptor> tools)
    {
        var validation = LowLevelValidator.Validate(BuildPlan(), _outlinePlan, tools);
        if (validation.IsValid)
        {
            return new JsonObject
            {
                ["tool"] = "low.validate",
                ["ok"] = true
            };
        }

        return new JsonObject
        {
            ["tool"] = "low.validate",
            ["ok"] = false,
            ["error"] = new JsonObject
            {
                ["code"] = "invalid_low_level",
                ["message"] = validation.Issues[0].Message,
                ["details"] = PlanningNodeJson.ToNode(validation.Issues)
            }
        };
    }

    private int FindStepIndex(string stepId)
    {
        var index = _steps.FindIndex(step => string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Low-level step '{stepId}' was not found.");
    }

    private void EnsureUniqueStepId(string stepId, int? excludedIndex)
    {
        var duplicateIndex = _steps.FindIndex(step => string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));
        if (duplicateIndex >= 0 && duplicateIndex != excludedIndex)
            throw new InvalidOperationException($"Low-level step id '{stepId}' already exists.");
    }

    private static LowLevelStep DeserializeStep(JsonNode? stepNode)
    {
        var step = stepNode is null
            ? throw new InvalidOperationException("Action input 'step' must be a valid low-level step object.")
            : PlanningNodeJson.DeserializeNode<LowLevelStep>(stepNode);

        if (string.IsNullOrWhiteSpace(step.Id))
            throw new InvalidOperationException("Low-level step id is required.");
        if (string.IsNullOrWhiteSpace(step.OutlineNodeId))
            throw new InvalidOperationException("Low-level step outlineNodeId is required.");
        if (string.IsNullOrWhiteSpace(step.Kind))
            throw new InvalidOperationException("Low-level step kind is required.");
        if (string.IsNullOrWhiteSpace(step.Purpose))
            throw new InvalidOperationException("Low-level step purpose is required.");

        return CloneStep(step, isResult: false);
    }

    private static LowLevelInputSource DeserializeSource(JsonNode? sourceNode)
    {
        var source = sourceNode is null
            ? throw new InvalidOperationException("Action input 'source' must be a valid low-level input source object.")
            : PlanningNodeJson.DeserializeNode<LowLevelInputSource>(sourceNode);

        if (string.IsNullOrWhiteSpace(source.Kind))
            throw new InvalidOperationException("Low-level input source kind is required.");

        return new LowLevelInputSource
        {
            Kind = source.Kind.Trim(),
            Value = PlanningNodeJson.CloneNode(source.Value),
            StepId = NormalizeOptional(source.StepId),
            Port = NormalizeOptional(source.Port),
            Mode = NormalizeOptional(source.Mode)
        };
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

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LowLevelStep CloneStep(LowLevelStep step, bool isResult) => new()
    {
        Id = step.Id.Trim(),
        OutlineNodeId = step.OutlineNodeId.Trim(),
        Kind = step.Kind.Trim(),
        CapabilityId = NormalizeOptional(step.CapabilityId),
        Purpose = step.Purpose.Trim(),
        Inputs = [.. step.Inputs.Select(static input => new LowLevelStepInput
        {
            Name = input.Name.Trim(),
            Source = new LowLevelInputSource
            {
                Kind = input.Source.Kind.Trim(),
                Value = PlanningNodeJson.CloneNode(input.Source.Value),
                StepId = NormalizeOptional(input.Source.StepId),
                Port = NormalizeOptional(input.Source.Port),
                Mode = NormalizeOptional(input.Source.Mode)
            }
        })],
        Outputs = [.. step.Outputs.Select(static output => new LowLevelStepOutput
        {
            Name = output.Name.Trim(),
            SemanticType = output.SemanticType.Trim()
        })],
        Fanout = string.IsNullOrWhiteSpace(step.Fanout) ? LowLevelFanoutModes.Single : step.Fanout.Trim(),
        Out = step.Out is null
            ? null
            : new LowLevelStepOutputSettings
            {
                Format = step.Out.Format.Trim()
            },
        IsResult = isResult
    };

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
