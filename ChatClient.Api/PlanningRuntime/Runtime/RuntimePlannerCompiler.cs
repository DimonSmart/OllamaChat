using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Runtime;

public sealed class RuntimeCompileResult
{
    public RuntimePlan? Plan { get; init; }

    public List<PlanningIssue> Issues { get; init; } = [];

    public bool IsSuccess => Plan is not null && Issues.Count == 0;
}

public interface IRuntimePlanCompiler
{
    RuntimeCompileResult Compile(LowLevelPlan plan);
}

public sealed class RuntimePlannerCompiler : IRuntimePlanCompiler
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

        var issues = new List<PlanningIssue>();
        var tools = _toolsById.Values.ToList();
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
            runtimeSteps.Add(CompileStep(step, plan, issues, resolvedResultStepId));

        var runtimePlan = new RuntimePlan
        {
            Goal = plan.Goal,
            ResultStepId = resolvedResultStepId,
            ResultPort = resultStep.Outputs[0].Name,
            Steps = runtimeSteps
        };

        var bindingIssues = new LowLevelBindingCompatibilityValidator(plan, tools, outlinePlan: null, issueLayer: "runtime").Validate();
        issues.AddRange(bindingIssues);

        var validation = RuntimePlanValidator.Validate(runtimePlan, tools);
        issues.AddRange(validation.Issues);
        return issues.Count == 0
            ? new RuntimeCompileResult { Plan = runtimePlan, Issues = issues }
            : new RuntimeCompileResult { Issues = issues };
    }

    private RuntimeStep CompileStep(
        LowLevelStep step,
        LowLevelPlan plan,
        List<PlanningIssue> issues,
        string resolvedResultStepId)
    {
        _toolsById.TryGetValue(step.CapabilityId ?? string.Empty, out var tool);

        if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(step.CapabilityId)
            && tool is null)
        {
            issues.Add(CreateIssue("capability_unknown", $"Low-level step '{step.Id}' references unknown capability '{step.CapabilityId}'."));
        }

        var runtimeInputs = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase);
        var mappedInputCount = 0;
        var effectiveFanout = ResolveEffectiveFanout(step, tool, plan);

        foreach (var input in step.Inputs)
        {
            var normalizedInputName = NormalizeToolInputName(step, tool, input.Name);
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

                _toolsById.TryGetValue(sourceStep.CapabilityId ?? string.Empty, out var sourceTool);
                normalizedSource = NormalizeCollectionInputMode(normalizedSource, effectiveFanout, sourceStep, sourcePort, sourceTool);
                if (string.Equals(normalizedSource.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase))
                    mappedInputCount++;
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

        NormalizeBuiltInToolInputs(step, tool, plan, runtimeInputs);

        if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            && tool is not null)
        {
            foreach (var inputName in runtimeInputs.Keys)
            {
                if (!ToolInputExists(tool, inputName))
                    issues.Add(CreateIssue("tool_input_unknown", $"Tool step '{step.Id}' uses unknown input '{inputName}' for capability '{step.CapabilityId}'."));
            }

            ValidateToolInputSchema(step, tool, runtimeInputs, issues);
        }

        var runtimeOutputs = step.Outputs.Select(output => new RuntimeStepOutput
        {
            Name = output.Name,
            SemanticType = ResolveRuntimeSemanticType(step, tool, output, effectiveFanout)
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
        AppToolDescriptor? tool,
        LowLevelPlan plan,
        IDictionary<string, RuntimeInputValue> runtimeInputs)
    {
        if (!string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            || tool is null)
        {
            return;
        }

        if (RuntimeToolCapabilityMatcher.IsBuiltInWebSearch(tool, step.CapabilityId))
        {
            if (!runtimeInputs.ContainsKey("query") && !string.IsNullOrWhiteSpace(plan.Goal))
                runtimeInputs["query"] = CreateLiteralInput(System.Text.Json.Nodes.JsonValue.Create(plan.Goal.Trim()));

            if (!runtimeInputs.ContainsKey("limit"))
                runtimeInputs["limit"] = CreateLiteralInput(System.Text.Json.Nodes.JsonValue.Create(6));
        }
    }

    private string ResolveEffectiveFanout(LowLevelStep step, AppToolDescriptor? tool, LowLevelPlan plan)
    {
        if (!string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            || !RuntimeToolCapabilityMatcher.IsBuiltInWebDownload(tool, step.CapabilityId)
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
            if (sourceStep is not null && sourcePort is not null)
            {
                _toolsById.TryGetValue(sourceStep.CapabilityId ?? string.Empty, out var sourceTool);
                if (IsCollectionOutput(sourceStep, sourcePort, sourceTool))
                    return LowLevelFanoutModes.PerItem;
            }
        }

        return step.Fanout;
    }

    private static LowLevelInputSource NormalizeCollectionInputMode(
        LowLevelInputSource source,
        string fanout,
        LowLevelStep sourceStep,
        LowLevelStepOutput sourcePort,
        AppToolDescriptor? sourceTool)
    {
        if (!string.Equals(fanout, LowLevelFanoutModes.PerItem, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase)
            || !IsCollectionOutput(sourceStep, sourcePort, sourceTool))
        {
            return source;
        }

        return new LowLevelInputSource
        {
            Kind = source.Kind,
            Value = PlanningNodeJson.CloneNode(source.Value),
            StepId = source.StepId,
            Port = source.Port,
            Mode = LowLevelInputModes.Map
        };
    }

    private static string? ResolveResultStepId(LowLevelPlan plan, List<PlanningIssue> issues)
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

    private static void ValidateToolInputSchema(
        LowLevelStep step,
        AppToolDescriptor tool,
        IReadOnlyDictionary<string, RuntimeInputValue> runtimeInputs,
        List<PlanningIssue> issues)
    {
        var draftInputs = BuildDraftToolInputs(tool, runtimeInputs);
        var schemaIssues = ToolInputSchemaValidator.ValidateDraftInput(draftInputs, tool.InputSchema);
        if (schemaIssues.Count == 0)
            return;

        var message = string.Join(
            " ",
            schemaIssues
                .Select(static issue => issue.Message)
                .Distinct(StringComparer.Ordinal));
        issues.Add(CreateIssue(
            "tool_input_schema_mismatch",
            $"Tool step '{step.Id}' does not satisfy the input schema for capability '{step.CapabilityId}': {message}"));
    }

    private static JsonObject BuildDraftToolInputs(
        AppToolDescriptor tool,
        IReadOnlyDictionary<string, RuntimeInputValue> runtimeInputs)
    {
        var result = new JsonObject();
        var propertySchemas = tool.InputSchema.ValueKind == JsonValueKind.Object
            && tool.InputSchema.TryGetProperty("properties", out var propertiesElement)
            && propertiesElement.ValueKind == JsonValueKind.Object
            ? propertiesElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var input in runtimeInputs)
        {
            if (string.Equals(input.Value.Kind, RuntimeInputValueKinds.Literal, StringComparison.OrdinalIgnoreCase))
            {
                result[input.Key] = PlanningNodeJson.CloneNode(input.Value.Literal);
                continue;
            }

            if (propertySchemas is not null && propertySchemas.TryGetValue(input.Key, out var propertySchema))
            {
                result[input.Key] = CreateSchemaPlaceholder(propertySchema);
                continue;
            }

            result[input.Key] = JsonValue.Create("<binding>");
        }

        return result;
    }

    private static JsonNode? CreateSchemaPlaceholder(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return JsonValue.Create("<value>");

        if (schema.TryGetProperty("enum", out var enumElement)
            && enumElement.ValueKind == JsonValueKind.Array)
        {
            var firstEnumValue = enumElement.EnumerateArray().FirstOrDefault();
            if (firstEnumValue.ValueKind != JsonValueKind.Undefined)
                return JsonNode.Parse(firstEnumValue.GetRawText());
        }

        if (PlanStepOutputContractResolver.TryGetSchemaTypes(schema, out var types) && types.Count > 0)
        {
            if (types.Contains("object", StringComparer.OrdinalIgnoreCase))
                return CreateObjectPlaceholder(schema);
            if (types.Contains("array", StringComparer.OrdinalIgnoreCase))
                return CreateArrayPlaceholder(schema);
            if (types.Contains("string", StringComparer.OrdinalIgnoreCase))
                return JsonValue.Create(string.Empty);
            if (types.Contains("integer", StringComparer.OrdinalIgnoreCase))
                return JsonValue.Create(0);
            if (types.Contains("number", StringComparer.OrdinalIgnoreCase))
                return JsonValue.Create(0);
            if (types.Contains("boolean", StringComparer.OrdinalIgnoreCase))
                return JsonValue.Create(false);
            if (types.Contains("null", StringComparer.OrdinalIgnoreCase))
                return null;
        }

        if (schema.TryGetProperty("properties", out var propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object)
            return CreateObjectPlaceholder(schema);

        if (schema.TryGetProperty("items", out _))
            return CreateArrayPlaceholder(schema);

        return JsonValue.Create("<value>");
    }

    private static JsonObject CreateObjectPlaceholder(JsonElement schema)
    {
        var result = new JsonObject();
        if (!schema.TryGetProperty("required", out var requiredElement)
            || requiredElement.ValueKind != JsonValueKind.Array
            || !schema.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        var propertySchemas = propertiesElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var requiredProperty in requiredElement.EnumerateArray())
        {
            var propertyName = requiredProperty.GetString();
            if (string.IsNullOrWhiteSpace(propertyName) || !propertySchemas.TryGetValue(propertyName, out var propertySchema))
                continue;

            result[propertyName] = CreateSchemaPlaceholder(propertySchema);
        }

        return result;
    }

    private static JsonArray CreateArrayPlaceholder(JsonElement schema)
    {
        var result = new JsonArray();
        if (schema.TryGetProperty("items", out var itemsElement))
            result.Add(CreateSchemaPlaceholder(itemsElement));

        return result;
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
            Value = PlanningNodeJson.CloneNode(source.Value),
            StepId = source.StepId,
            Port = source.Port,
            Mode = normalizedMode
        };
    }

    private static string NormalizeToolInputName(LowLevelStep step, AppToolDescriptor? tool, string inputName)
    {
        if (!string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            || tool is null)
        {
            return inputName;
        }

        if (RuntimeToolCapabilityMatcher.IsBuiltInWebDownload(tool, step.CapabilityId))
        {
            var normalized = inputName.Trim();
            if (normalized.Contains("url", StringComparison.OrdinalIgnoreCase))
                return "url";

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

    private static string ResolveRuntimeSemanticType(LowLevelStep step, AppToolDescriptor? tool, LowLevelStepOutput output, string fanout)
    {
        var normalized = NormalizeSemanticType(output.SemanticType);
        if (ShouldTreatOutputAsCollection(step, tool, output, fanout))
            return EnsureArraySemanticType(normalized);

        return normalized;
    }

    private static bool IsCollectionOutput(LowLevelStep step, LowLevelStepOutput output, AppToolDescriptor? tool)
        => ShouldTreatOutputAsCollection(step, tool, output, step.Fanout);

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

    private static bool ShouldTreatOutputAsCollection(
        LowLevelStep step,
        AppToolDescriptor? tool,
        LowLevelStepOutput output,
        string fanout)
    {
        if (string.Equals(fanout, LowLevelFanoutModes.PerItem, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
            && RuntimeToolCapabilityMatcher.IsBuiltInWebSearch(tool, step.CapabilityId))
        {
            return true;
        }

        return LooksLikeCollectionSemanticHint(output.SemanticType)
            || LooksLikeCollectionPortName(output.Name);
    }

    private static bool LooksLikeCollectionSemanticHint(string semanticType)
    {
        var normalized = NormalizeSemanticType(semanticType);
        if (normalized.EndsWith("[]", StringComparison.Ordinal))
            return true;

        return HasCollectionSuffix(normalized);
    }

    private static bool LooksLikeCollectionPortName(string portName) =>
        !string.IsNullOrWhiteSpace(portName)
        && HasCollectionSuffix(portName.Trim());

    private static bool HasCollectionSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized.EndsWith("list", StringComparison.Ordinal)
            || normalized.EndsWith("items", StringComparison.Ordinal)
            || normalized.EndsWith("results", StringComparison.Ordinal)
            || normalized.EndsWith("references", StringComparison.Ordinal)
            || normalized.EndsWith("documents", StringComparison.Ordinal)
            || normalized.EndsWith("pages", StringComparison.Ordinal)
            || normalized.EndsWith("candidates", StringComparison.Ordinal)
            || normalized.EndsWith("records", StringComparison.Ordinal)
            || normalized.EndsWith("entries", StringComparison.Ordinal)
            || normalized.EndsWith("urls", StringComparison.Ordinal)
            || normalized.EndsWith("links", StringComparison.Ordinal)
            || normalized.EndsWith("sources", StringComparison.Ordinal)
            || normalized.EndsWith("artifacts", StringComparison.Ordinal);
    }

    private static RuntimeInputValue CreateLiteralInput(System.Text.Json.Nodes.JsonNode? value) =>
        new()
        {
            Kind = RuntimeInputValueKinds.Literal,
            Literal = value
        };

    private static PlanningIssue CreateIssue(string code, string message) =>
        new()
        {
            Code = code,
            Message = message,
            Layer = "runtime"
        };
}
