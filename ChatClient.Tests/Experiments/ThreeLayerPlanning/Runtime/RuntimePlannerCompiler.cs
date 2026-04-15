using ChatClient.Api.Services;
using System.Text.Json.Nodes;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Runtime;

public sealed class RuntimeCompileResult
{
    public RuntimePlan? Plan { get; init; }

    public List<ExperimentIssue> Issues { get; init; } = [];

    public bool IsSuccess => Plan is not null && Issues.Count == 0;
}

public sealed class RuntimePlannerCompiler
{
    private readonly IReadOnlyDictionary<string, AppToolDescriptor> _toolsById;

    public RuntimePlannerCompiler(IReadOnlyCollection<AppToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _toolsById = tools.ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);
    }

    public RuntimeCompileResult Compile(LowLevelPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var issues = new List<ExperimentIssue>();
        var resolvedResultStepId = ResolveResultStepId(plan, issues);
        if (string.IsNullOrWhiteSpace(resolvedResultStepId))
            return new RuntimeCompileResult { Issues = issues };

        var resultStep = plan.Steps.FirstOrDefault(step => string.Equals(step.Id, resolvedResultStepId, StringComparison.OrdinalIgnoreCase));
        if (resultStep is null)
        {
            issues.Add(CreateIssue("result_step_unknown", $"Low-level resultStepId '{resolvedResultStepId}' does not exist."));
            return new RuntimeCompileResult { Issues = issues };
        }

        if (resultStep.Outputs.Count != 1)
        {
            issues.Add(CreateIssue("result_port_ambiguous", $"Result step '{resultStep.Id}' must have exactly one output port."));
            return new RuntimeCompileResult { Issues = issues };
        }

        var runtimeSteps = new List<RuntimeStep>(plan.Steps.Count);
        foreach (var step in plan.Steps)
        {
            runtimeSteps.Add(CompileStep(step, plan, issues, resolvedResultStepId));
        }

        var runtimePlan = new RuntimePlan
        {
            Goal = plan.Goal,
            ResultStepId = resolvedResultStepId,
            ResultPort = resultStep.Outputs[0].Name,
            Steps = runtimeSteps
        };

        var validation = RuntimePlanValidator.Validate(runtimePlan, _toolsById.Values.ToList());
        issues.AddRange(validation.Issues);
        return issues.Count == 0
            ? new RuntimeCompileResult { Plan = runtimePlan, Issues = issues }
            : new RuntimeCompileResult { Issues = issues };
    }

    private RuntimeStep CompileStep(
        LowLevelStep step,
        LowLevelPlan plan,
        List<ExperimentIssue> issues,
        string resolvedResultStepId)
    {
        if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(step.CapabilityId)
            && !_toolsById.ContainsKey(step.CapabilityId))
        {
            issues.Add(CreateIssue("capability_unknown", $"Low-level step '{step.Id}' references unknown capability '{step.CapabilityId}'."));
        }

        var runtimeInputs = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase);
        var mappedInputCount = 0;
        var effectiveFanout = ResolveEffectiveFanout(step, plan);

        foreach (var input in step.Inputs)
        {
            var normalizedInputName = NormalizeToolInputName(step, input.Name);
            var normalizedSource = NormalizeInputSource(effectiveFanout, input.Source);
            if (string.Equals(input.Source.Kind, LowLevelInputSourceKinds.StepOutputPort, StringComparison.OrdinalIgnoreCase))
            {
                var sourceStep = plan.Steps.FirstOrDefault(candidate => string.Equals(candidate.Id, normalizedSource.StepId, StringComparison.OrdinalIgnoreCase));
                var sourcePort = sourceStep?.Outputs.FirstOrDefault(output => string.Equals(output.Name, normalizedSource.Port, StringComparison.OrdinalIgnoreCase));
                if (sourceStep is null || sourcePort is null)
                {
                    issues.Add(CreateIssue("binding_source_missing", $"Low-level step '{step.Id}' input '{input.Name}' references an unknown step or port."));
                    continue;
                }

                normalizedSource = NormalizeCollectionInputMode(normalizedSource, effectiveFanout, sourceStep, sourcePort);
                if (string.Equals(normalizedSource.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase))
                {
                    mappedInputCount++;
                    if (!IsCollectionOutput(sourceStep, sourcePort))
                    {
                        issues.Add(CreateIssue("binding_map_non_array", $"Low-level step '{step.Id}' input '{input.Name}' uses map mode on non-array semantic type '{sourcePort.SemanticType}'."));
                    }
                }
            }

            runtimeInputs[normalizedInputName] = RuntimeBindingResolver.Resolve(normalizedSource);
        }

        if (string.Equals(effectiveFanout, LowLevelFanoutModes.PerItem, StringComparison.OrdinalIgnoreCase)
            && mappedInputCount == 0)
        {
            issues.Add(CreateIssue("fanout_requires_map", $"Low-level step '{step.Id}' is per_item but has no mapped input."));
        }

        if (mappedInputCount > 1)
            issues.Add(CreateIssue("multiple_mapped_inputs_unsupported", $"Low-level step '{step.Id}' has more than one mapped input."));

        NormalizeBuiltInToolInputs(step, plan, runtimeInputs);

        if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(step.CapabilityId)
            && _toolsById.TryGetValue(step.CapabilityId, out var tool))
        {
            foreach (var inputName in runtimeInputs.Keys)
            {
                if (!ToolInputExists(tool, inputName))
                    issues.Add(CreateIssue("tool_input_unknown", $"Tool step '{step.Id}' uses unknown input '{inputName}' for capability '{step.CapabilityId}'."));
            }
        }

        var runtimeOutputs = step.Outputs.Select(output => new RuntimeStepOutput
        {
            Name = output.Name,
            SemanticType = ResolveRuntimeSemanticType(step, output, effectiveFanout)
        }).ToList();

        return new RuntimeStep
        {
            Id = step.Id,
            Kind = step.Kind,
            CapabilityId = step.CapabilityId,
            Purpose = step.Purpose,
            Instruction = step.Purpose,
            Fanout = effectiveFanout,
            In = runtimeInputs,
            Outputs = runtimeOutputs,
            Out = step.Out is null
                ? null
                : new RuntimeStepOutputSettings
                {
                    Format = step.Out.Format
                },
            IsResult = string.Equals(step.Id, resolvedResultStepId, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static void NormalizeBuiltInToolInputs(
        LowLevelStep step,
        LowLevelPlan plan,
        IDictionary<string, RuntimeInputValue> runtimeInputs)
    {
        if (!string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(step.CapabilityId))
        {
            return;
        }

        if (string.Equals(step.CapabilityId, "built-in-web:search", StringComparison.OrdinalIgnoreCase))
        {
            if (!runtimeInputs.ContainsKey("query") && !string.IsNullOrWhiteSpace(plan.Goal))
            {
                runtimeInputs["query"] = CreateLiteralInput(JsonValue.Create(plan.Goal.Trim()));
            }

            if (!runtimeInputs.ContainsKey("limit"))
                runtimeInputs["limit"] = CreateLiteralInput(JsonValue.Create(6));
        }
    }

    private static string ResolveEffectiveFanout(LowLevelStep step, LowLevelPlan plan)
    {
        if (!string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(step.CapabilityId, "built-in-web:download", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(step.Fanout, LowLevelFanoutModes.Single, StringComparison.OrdinalIgnoreCase))
        {
            return step.Fanout;
        }

        foreach (var input in step.Inputs)
        {
            if (!string.Equals(input.Source.Kind, LowLevelInputSourceKinds.StepOutputPort, StringComparison.OrdinalIgnoreCase))
                continue;

            var sourceStep = plan.Steps.FirstOrDefault(candidate => string.Equals(candidate.Id, input.Source.StepId, StringComparison.OrdinalIgnoreCase));
            var sourcePort = sourceStep?.Outputs.FirstOrDefault(output => string.Equals(output.Name, input.Source.Port, StringComparison.OrdinalIgnoreCase));
            if (sourceStep is not null && sourcePort is not null && IsCollectionOutput(sourceStep, sourcePort))
                return LowLevelFanoutModes.PerItem;
        }

        return step.Fanout;
    }

    private static LowLevelInputSource NormalizeCollectionInputMode(
        LowLevelInputSource source,
        string fanout,
        LowLevelStep sourceStep,
        LowLevelStepOutput sourcePort)
    {
        if (!string.Equals(fanout, LowLevelFanoutModes.PerItem, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase)
            || !IsCollectionOutput(sourceStep, sourcePort))
        {
            return source;
        }

        return new LowLevelInputSource
        {
            Kind = source.Kind,
            Value = source.Value?.DeepClone(),
            StepId = source.StepId,
            Port = source.Port,
            Mode = LowLevelInputModes.Map
        };
    }

    private static string? ResolveResultStepId(LowLevelPlan plan, List<ExperimentIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(plan.ResultStepId))
            return plan.ResultStepId;

        var resultSteps = plan.Steps.Where(static step => step.IsResult).ToList();
        if (resultSteps.Count == 1)
            return resultSteps[0].Id;

        issues.Add(CreateIssue("result_step_missing", "Could not resolve a single low-level result step."));
        return null;
    }

    private static bool ToolInputExists(AppToolDescriptor tool, string inputName)
    {
        if (tool.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Object)
            return true;

        if (tool.InputSchema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (string.Equals(property.Name, inputName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        return true;
    }

    private static LowLevelInputSource NormalizeInputSource(string fanout, LowLevelInputSource source)
    {
        if (!string.Equals(source.Kind, LowLevelInputSourceKinds.StepOutputPort, StringComparison.OrdinalIgnoreCase))
            return source;

        var normalizedMode = source.Mode;
        if (string.Equals(fanout, LowLevelFanoutModes.Single, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase))
        {
            normalizedMode = LowLevelInputModes.Value;
        }

        return new LowLevelInputSource
        {
            Kind = source.Kind,
            Value = source.Value?.DeepClone(),
            StepId = source.StepId,
            Port = source.Port,
            Mode = normalizedMode
        };
    }

    private static string NormalizeToolInputName(LowLevelStep step, string inputName)
    {
        if (!string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(step.CapabilityId))
        {
            return inputName;
        }

        if (string.Equals(step.CapabilityId, "built-in-web:download", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = inputName.Trim();
            if (normalized.Contains("url", StringComparison.OrdinalIgnoreCase))
            {
                return "url";
            }

            if (normalized.Contains("page", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("ref", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("result", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("candidate", StringComparison.OrdinalIgnoreCase))
            {
                return "page";
            }
        }

        return inputName;
    }

    private static string ResolveRuntimeSemanticType(LowLevelStep step, LowLevelStepOutput output, string fanout)
    {
        var normalized = NormalizeSemanticType(output.SemanticType);
        if (string.Equals(fanout, LowLevelFanoutModes.PerItem, StringComparison.OrdinalIgnoreCase))
            return EnsureArraySemanticType(normalized);

        if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            && string.Equals(step.CapabilityId, "built-in-web:search", StringComparison.OrdinalIgnoreCase))
        {
            return EnsureArraySemanticType(normalized);
        }

        return normalized;
    }

    private static bool IsCollectionOutput(LowLevelStep step, LowLevelStepOutput output)
    {
        if (string.Equals(step.Fanout, LowLevelFanoutModes.PerItem, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            && string.Equals(step.CapabilityId, "built-in-web:search", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = NormalizeSemanticType(output.SemanticType);
        return normalized.EndsWith("[]", StringComparison.Ordinal);
    }

    private static string NormalizeSemanticType(string semanticType)
    {
        if (string.IsNullOrWhiteSpace(semanticType))
            return semanticType;

        var normalized = semanticType.Trim();
        return normalized.EndsWith("List", StringComparison.OrdinalIgnoreCase)
            ? $"{normalized[..^4]}[]"
            : normalized;
    }

    private static string EnsureArraySemanticType(string semanticType) =>
        semanticType.EndsWith("[]", StringComparison.Ordinal)
            ? semanticType
            : $"{semanticType}[]";

    private static RuntimeInputValue CreateLiteralInput(JsonNode? value) =>
        new()
        {
            Kind = RuntimeInputValueKinds.Literal,
            Literal = value
        };

    private static ExperimentIssue CreateIssue(string code, string message) =>
        new()
        {
            Code = code,
            Message = message,
            Layer = "runtime"
        };
}
