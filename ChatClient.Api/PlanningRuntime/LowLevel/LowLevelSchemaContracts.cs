using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.LowLevel;

internal sealed class LowLevelToolSchemaResolver
{
    private readonly IReadOnlyDictionary<string, AppToolDescriptor> _toolsById;

    public LowLevelToolSchemaResolver(IReadOnlyCollection<AppToolDescriptor> tools)
    {
        _toolsById = tools.ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);
    }

    public AppToolDescriptor? ResolveTool(string? capabilityId)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
            return null;

        _toolsById.TryGetValue(capabilityId, out var tool);
        return tool;
    }

    public string NormalizeInputName(LowLevelStep step, AppToolDescriptor? tool, string inputName)
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

    public bool TryGetInputSchema(AppToolDescriptor tool, string inputName, out JsonElement schema)
    {
        schema = default;
        if (tool.InputSchema.ValueKind != JsonValueKind.Object
            || !tool.InputSchema.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in propertiesElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, inputName, StringComparison.OrdinalIgnoreCase))
                continue;

            schema = property.Value.Clone();
            return true;
        }

        return false;
    }

    public IReadOnlyList<string> FindSuggestedCapabilityIds(
        LowLevelStep targetStep,
        AppToolDescriptor? targetTool,
        string targetInputName,
        JsonElement actualSchema)
    {
        if (targetTool is null
            || !RuntimeToolCapabilityMatcher.IsBuiltInWebSearch(targetTool, targetStep.CapabilityId)
            || !string.Equals(targetInputName, "query", StringComparison.OrdinalIgnoreCase)
            || !SchemaCompatibilityInspector.DescribeSchemaShape(actualSchema).StartsWith("object", StringComparison.OrdinalIgnoreCase)
            || !SchemaHasUrlProperty(actualSchema))
        {
            return [];
        }

        return _toolsById.Values
            .Where(tool => RuntimeToolCapabilityMatcher.IsBuiltInWebDownload(tool, tool.QualifiedName))
            .Select(static tool => tool.QualifiedName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool SchemaHasUrlProperty(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("properties", out var propertiesElement)
        && propertiesElement.ValueKind == JsonValueKind.Object
        && propertiesElement.TryGetProperty("url", out _);
}

internal sealed class LowLevelStepOutputContractResolver(LowLevelPlan plan, IReadOnlyCollection<AppToolDescriptor> tools)
{
    private readonly LowLevelPlan _plan = plan ?? throw new ArgumentNullException(nameof(plan));
    private readonly LowLevelToolSchemaResolver _toolSchemas = new(tools);

    public bool TryResolveFinalSchema(LowLevelStep step, string portName, out JsonElement schema)
    {
        schema = default;
        if (!step.Outputs.Any(output => string.Equals(output.Name, portName, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!TryResolveCallSchema(step, out var callSchema))
            return false;

        schema = string.Equals(step.Fanout, LowLevelFanoutModes.PerItem, StringComparison.OrdinalIgnoreCase)
            ? BuildFinalSchemaForMappedCalls(callSchema)
            : callSchema;
        return true;
    }

    private bool TryResolveCallSchema(LowLevelStep step, out JsonElement schema)
    {
        schema = default;

        if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase))
        {
            var tool = _toolSchemas.ResolveTool(step.CapabilityId);
            if (tool?.OutputSchema is not { } toolOutputSchema)
                return false;

            if (RuntimeToolCapabilityMatcher.IsBuiltInWebSearch(tool, step.CapabilityId)
                && TryGetObjectProperty(toolOutputSchema, "results", out var resultsSchema))
            {
                schema = resultsSchema;
                return true;
            }

            schema = toolOutputSchema.Clone();
            return true;
        }

        if (step.Out is null || string.IsNullOrWhiteSpace(step.Out.Format))
            return false;

        if (string.Equals(step.Out.Format, RuntimeOutputFormats.String, StringComparison.OrdinalIgnoreCase))
        {
            schema = PlanStepOutputContractResolver.CreateStringSchema();
            return true;
        }

        if (TryResolveJsonLlmSchema(step, out schema))
            return true;

        schema = PlanStepOutputContractResolver.CreateOpaqueSchema();
        return true;
    }

    private bool TryResolveJsonLlmSchema(LowLevelStep sourceStep, out JsonElement schema)
    {
        schema = default;

        var candidateSchemas = new List<JsonElement>();
        foreach (var consumerStep in _plan.Steps)
        {
            foreach (var input in consumerStep.Inputs)
            {
                if (!string.Equals(input.Source.Kind, LowLevelInputSourceKinds.StepOutputPort, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(input.Source.StepId, sourceStep.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var consumerTool = _toolSchemas.ResolveTool(consumerStep.CapabilityId);
                if (consumerTool is null)
                    continue;

                var normalizedInputName = _toolSchemas.NormalizeInputName(consumerStep, consumerTool, input.Name);
                if (!_toolSchemas.TryGetInputSchema(consumerTool, normalizedInputName, out var inputSchema))
                    continue;

                if (string.Equals(input.Source.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase))
                {
                    candidateSchemas.Add(PlanStepOutputContractResolver.CreateArraySchema(inputSchema));
                }
                else
                {
                    candidateSchemas.Add(inputSchema);
                }
            }
        }

        if (candidateSchemas.Count == 0)
            return false;

        var distinct = candidateSchemas
            .GroupBy(static item => item.GetRawText(), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
        if (distinct.Count != 1)
            return false;

        schema = distinct[0].Clone();
        return true;
    }

    private static JsonElement BuildFinalSchemaForMappedCalls(JsonElement callSchema)
    {
        if (PlanStepOutputContractResolver.TryGetArrayItemSchema(callSchema, out var itemSchema))
            return PlanStepOutputContractResolver.CreateArraySchema(itemSchema);

        return PlanStepOutputContractResolver.CreateArraySchema(callSchema);
    }

    private static bool TryGetObjectProperty(JsonElement schema, string propertyName, out JsonElement propertySchema)
    {
        propertySchema = default;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object
            || !propertiesElement.TryGetProperty(propertyName, out propertySchema))
        {
            return false;
        }

        propertySchema = propertySchema.Clone();
        return true;
    }
}

internal sealed class LowLevelBindingCompatibilityValidator(
    LowLevelPlan plan,
    IReadOnlyCollection<AppToolDescriptor> tools,
    OutlinePlan? outlinePlan = null,
    string issueLayer = "low_level")
{
    private readonly LowLevelPlan _plan = plan ?? throw new ArgumentNullException(nameof(plan));
    private readonly IReadOnlyDictionary<string, OutlineNode> _outlineById = outlinePlan?.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, OutlineNode>(StringComparer.OrdinalIgnoreCase);
    private readonly LowLevelToolSchemaResolver _toolSchemas = new(tools);
    private readonly LowLevelStepOutputContractResolver _outputContracts = new(plan, tools);
    private readonly string _issueLayer = issueLayer;

    public IReadOnlyList<PlanningIssue> Validate()
    {
        var issues = new List<PlanningIssue>();

        foreach (var step in _plan.Steps)
        {
            var targetTool = _toolSchemas.ResolveTool(step.CapabilityId);
            foreach (var input in step.Inputs)
            {
                if (!string.Equals(input.Source.Kind, LowLevelInputSourceKinds.StepOutputPort, StringComparison.OrdinalIgnoreCase))
                    continue;

                var sourceStep = _plan.Steps.FirstOrDefault(candidate => string.Equals(candidate.Id, input.Source.StepId, StringComparison.OrdinalIgnoreCase));
                if (sourceStep is null)
                    continue;

                var sourcePort = sourceStep.Outputs.FirstOrDefault(output => string.Equals(output.Name, input.Source.Port, StringComparison.OrdinalIgnoreCase));
                if (sourcePort is null || !_outputContracts.TryResolveFinalSchema(sourceStep, sourcePort.Name, out var sourceSchema))
                    continue;

                var actualSchema = sourceSchema.Clone();
                var mode = string.IsNullOrWhiteSpace(input.Source.Mode) ? LowLevelInputModes.Value : input.Source.Mode!;
                if (string.Equals(mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase))
                {
                    if (!PlanStepOutputContractResolver.TryGetArrayItemSchema(sourceSchema, out var itemSchema))
                    {
                        issues.Add(CreateIssue(
                            "binding_map_non_array",
                            $"Low-level step '{step.Id}' input '{input.Name}' uses map mode on non-array source '{sourceStep.Id}.{sourcePort.Name}'.",
                            BuildDetails(step, input, sourceStep, sourcePort, null, sourceSchema, null, null, null)));
                        continue;
                    }

                    actualSchema = itemSchema;
                }

                if (targetTool is null)
                    continue;

                var normalizedInputName = _toolSchemas.NormalizeInputName(step, targetTool, input.Name);
                if (!_toolSchemas.TryGetInputSchema(targetTool, normalizedInputName, out var expectedSchema))
                    continue;

                var compatibilityIssues = SchemaCompatibilityInspector.ValidateCompatibility(actualSchema, expectedSchema, normalizedInputName);
                if (compatibilityIssues.Count == 0)
                    continue;

                var suggestedCapabilityIds = _toolSchemas.FindSuggestedCapabilityIds(step, targetTool, normalizedInputName, actualSchema);
                var suggestedInputName = suggestedCapabilityIds.Count > 0 ? "page" : null;
                var suggestedBindingMode = suggestedCapabilityIds.Count > 0
                    ? (PlanStepOutputContractResolver.SchemaDefinesArray(sourceSchema) ? LowLevelInputModes.Map : LowLevelInputModes.Value)
                    : null;

                issues.Add(CreateIssue(
                    "binding_tool_schema_mismatch",
                    $"Low-level step '{step.Id}' input '{normalizedInputName}' is incompatible with the selected capability '{step.CapabilityId}': {string.Join(" ", compatibilityIssues)}",
                    BuildDetails(step, input, sourceStep, sourcePort, expectedSchema, actualSchema, suggestedCapabilityIds, suggestedInputName, suggestedBindingMode)));
            }
        }

        return issues;
    }

    private PlanningIssue CreateIssue(string code, string message, JsonElement details) =>
        new()
        {
            Layer = _issueLayer,
            Code = code,
            Message = message,
            Details = details,
            IsBlocking = true
        };

    private JsonElement BuildDetails(
        LowLevelStep step,
        LowLevelStepInput input,
        LowLevelStep sourceStep,
        LowLevelStepOutput sourcePort,
        JsonElement? expectedSchema,
        JsonElement? actualSchema,
        IReadOnlyList<string>? suggestedCapabilityIds,
        string? suggestedInputName,
        string? suggestedBindingMode)
    {
        _outlineById.TryGetValue(step.OutlineNodeId, out var outlineNode);

        return JsonSerializer.SerializeToElement(new
        {
            stepId = step.Id,
            inputName = input.Name,
            sourceStepId = sourceStep.Id,
            sourcePort = sourcePort.Name,
            expectedSchema = expectedSchema is JsonElement expected
                ? SchemaCompatibilityInspector.DescribeSchemaShape(expected)
                : "unknown",
            actualSchema = actualSchema is JsonElement actual
                ? SchemaCompatibilityInspector.DescribeSchemaShape(actual)
                : "unknown",
            outlineNodeKind = outlineNode?.Kind,
            capabilityId = step.CapabilityId,
            suggestedCapabilityIds = suggestedCapabilityIds ?? [],
            suggestedInputName,
            suggestedBindingMode
        });
    }
}
