using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using ModelContextProtocol.Protocol;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Runtime;

public sealed class RuntimeExecutionResult
{
    public bool Succeeded { get; init; }

    public string Status { get; init; } = string.Empty;

    public JsonNode? FinalOutput { get; init; }

    public List<PlanningIssue> Issues { get; init; } = [];
}

public interface IRuntimePlanExecutor
{
    Task<RuntimeExecutionResult> ExecuteAsync(
        RuntimePlan plan,
        CancellationToken cancellationToken = default);
}

public sealed class RuntimePlanExecutor : IRuntimePlanExecutor
{
    private readonly IPlanningLlmClient _llmClient;
    private readonly IReadOnlyDictionary<string, AppToolDescriptor> _toolsById;
    private readonly IExecutionLogger _log;
    private readonly IPlanRunObserver _observer;
    private readonly IMcpUserInteractionService? _mcpUserInteractionService;

    public RuntimePlanExecutor(
        IPlanningLlmClient llmClient,
        IReadOnlyCollection<AppToolDescriptor> tools,
        IExecutionLogger? executionLogger = null,
        IPlanRunObserver? planRunObserver = null,
        IMcpUserInteractionService? mcpUserInteractionService = null)
    {
        ArgumentNullException.ThrowIfNull(llmClient);
        ArgumentNullException.ThrowIfNull(tools);

        _llmClient = llmClient;
        _toolsById = tools.ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);
        _log = executionLogger ?? NullExecutionLogger.Instance;
        _observer = planRunObserver ?? NullPlanRunObserver.Instance;
        _mcpUserInteractionService = mcpUserInteractionService;
    }

    public async Task<RuntimeExecutionResult> ExecuteAsync(
        RuntimePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var stepOutputs = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<PlanningIssue>();

        foreach (var step in plan.Steps)
        {
            var resolvedInputs = ResolveInputs(step, stepOutputs, issues);
            if (issues.Count > 0)
                return new RuntimeExecutionResult { Status = "execution_failed", Issues = issues };

            JsonObject normalizedOutput;
            try
            {
                var resolvedInputElement = JsonSerializer.SerializeToElement(
                    resolvedInputs.ToDictionary(
                        static pair => pair.Key,
                        static pair => PlanningNodeJson.ToElement(pair.Value.Value)),
                    PlanningNodeJson.SerializerOptions);
                _log.Log($"[runtime] step:start id={step.Id} kind={step.Kind} capabilityId={step.CapabilityId ?? "<none>"}");
                _observer.OnEvent(new RuntimeStepStartedEvent(step.Id, step.Kind, resolvedInputElement));

                if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase))
                    normalizedOutput = await ExecuteToolStepAsync(step, resolvedInputs, issues, cancellationToken);
                else
                    normalizedOutput = await ExecuteLlmStepAsync(step, resolvedInputs, issues, cancellationToken);
            }
            catch (Exception ex)
            {
                issues.Add(CreateIssue("step_execution_exception", $"Runtime step '{step.Id}' failed: {ex.Message}"));
                _observer.OnEvent(new RuntimeStepCompletedEvent(step.Id, false, null, new ErrorInfo("step_execution_exception", ex.Message)));
                return new RuntimeExecutionResult { Status = "execution_failed", Issues = issues };
            }

            if (issues.Count > 0)
            {
                var lastIssue = issues[^1];
                _observer.OnEvent(new RuntimeStepCompletedEvent(step.Id, false, null, new ErrorInfo(lastIssue.Code, lastIssue.Message)));
                return new RuntimeExecutionResult { Status = "execution_failed", Issues = issues };
            }

            stepOutputs[step.Id] = normalizedOutput;
            _observer.OnEvent(new RuntimeStepCompletedEvent(
                step.Id,
                true,
                PlanningNodeJson.ToElement(normalizedOutput),
                null));
        }

        if (!stepOutputs.TryGetValue(plan.ResultStepId, out var resultStepOutput)
            || resultStepOutput[plan.ResultPort] is not JsonNode finalOutput)
        {
            issues.Add(CreateIssue("result_output_missing", $"Runtime result output '{plan.ResultStepId}.{plan.ResultPort}' is missing."));
            return new RuntimeExecutionResult { Status = "execution_failed", Issues = issues };
        }

        return new RuntimeExecutionResult
        {
            Succeeded = true,
            Status = "executed",
            FinalOutput = finalOutput.DeepClone(),
            Issues = issues
        };
    }

    private Dictionary<string, ResolvedInput> ResolveInputs(
        RuntimeStep step,
        IReadOnlyDictionary<string, JsonObject> stepOutputs,
        List<PlanningIssue> issues)
    {
        var result = new Dictionary<string, ResolvedInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in step.In)
        {
            if (string.Equals(input.Value.Kind, RuntimeInputValueKinds.Literal, StringComparison.OrdinalIgnoreCase))
            {
                result[input.Key] = new ResolvedInput(PlanningNodeJson.CloneNode(input.Value.Literal), LowLevelInputModes.Value);
                continue;
            }

            if (!RuntimeBindingResolver.TryParseBindingPath(input.Value.From ?? string.Empty, out var sourceStepId, out var port))
            {
                issues.Add(CreateIssue("binding_path_invalid", $"Runtime step '{step.Id}' input '{input.Key}' has invalid binding path '{input.Value.From}'."));
                continue;
            }

            if (!stepOutputs.TryGetValue(sourceStepId, out var sourceOutput) || sourceOutput[port] is not JsonNode sourceValue)
            {
                issues.Add(CreateIssue("binding_value_missing", $"Runtime step '{step.Id}' input '{input.Key}' could not resolve '{input.Value.From}'."));
                continue;
            }

            result[input.Key] = new ResolvedInput(sourceValue.DeepClone(), input.Value.Mode ?? LowLevelInputModes.Value);
        }

        return result;
    }

    private async Task<JsonObject> ExecuteToolStepAsync(
        RuntimeStep step,
        IReadOnlyDictionary<string, ResolvedInput> resolvedInputs,
        List<PlanningIssue> issues,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.CapabilityId) || !_toolsById.TryGetValue(step.CapabilityId, out var tool))
        {
            issues.Add(CreateIssue("tool_missing", $"Runtime tool step '{step.Id}' has unknown capability '{step.CapabilityId}'."));
            return new JsonObject();
        }

        var mappedInputs = resolvedInputs
            .Where(pair => string.Equals(pair.Value.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mappedInputs.Count > 1)
        {
            issues.Add(CreateIssue("multiple_mapped_inputs_unsupported", $"Runtime tool step '{step.Id}' has more than one mapped input."));
            return new JsonObject();
        }

        if (mappedInputs.Count == 1)
        {
            if (mappedInputs[0].Value.Value is not JsonArray items)
            {
                issues.Add(CreateIssue("mapped_input_not_array", $"Runtime tool step '{step.Id}' expected array input for '{mappedInputs[0].Key}'."));
                return new JsonObject();
            }

            var rawResults = new List<JsonNode?>(items.Count);
            foreach (var item in items)
            {
                try
                {
                    var arguments = BuildToolArguments(resolvedInputs, mappedInputs[0].Key, item?.DeepClone());
                    using var interactionScope = _mcpUserInteractionService?.BeginInteractionScope(McpInteractionScope.Planning);
                    var rawResult = await tool.ExecuteAsync(arguments, cancellationToken);
                    var envelope = NormalizeToolResult(tool, rawResult);
                    if (envelope.Ok)
                        rawResults.Add(envelope.Data is JsonElement mappedElement ? PlanningNodeJson.ToNode(mappedElement) : null);
                }
                catch
                {
                }
            }

            if (rawResults.Count == 0)
            {
                issues.Add(CreateIssue("mapped_tool_all_items_failed", $"Runtime tool step '{step.Id}' failed for every mapped input item."));
                return new JsonObject();
            }

            return NormalizeMappedOutput(step, rawResults);
        }

        var singleArguments = BuildToolArguments(resolvedInputs, null, null);
        using var singleInteractionScope = _mcpUserInteractionService?.BeginInteractionScope(McpInteractionScope.Planning);
        var singleResult = await tool.ExecuteAsync(singleArguments, cancellationToken);
        var normalizedResult = NormalizeToolResult(tool, singleResult);
        if (!normalizedResult.Ok)
        {
            issues.Add(CreateIssue(
                normalizedResult.Error?.Code ?? "tool_error",
                normalizedResult.Error?.Message ?? $"Tool step '{step.Id}' failed."));
            return new JsonObject();
        }

        return NormalizeSingleOutput(step, tool, normalizedResult.Data is JsonElement element ? PlanningNodeJson.ToNode(element) : null);
    }

    private async Task<JsonObject> ExecuteLlmStepAsync(
        RuntimeStep step,
        IReadOnlyDictionary<string, ResolvedInput> resolvedInputs,
        List<PlanningIssue> issues,
        CancellationToken cancellationToken)
    {
        var mappedInputs = resolvedInputs
            .Where(pair => string.Equals(pair.Value.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (string.Equals(step.Fanout, LowLevelFanoutModes.PerItem, StringComparison.OrdinalIgnoreCase))
        {
            if (mappedInputs.Count != 1)
            {
                issues.Add(CreateIssue("llm_mapped_input_invalid", $"Per-item LLM step '{step.Id}' must have exactly one mapped input."));
                return new JsonObject();
            }

            if (mappedInputs[0].Value.Value is not JsonArray mappedItems)
            {
                issues.Add(CreateIssue("llm_mapped_input_not_array", $"Per-item LLM step '{step.Id}' expected an array for '{mappedInputs[0].Key}'."));
                return new JsonObject();
            }

            var rawResults = new List<JsonNode?>(mappedItems.Count);
            foreach (var item in mappedItems)
            {
                var perItemInputs = BuildLlmInputs(resolvedInputs, mappedInputs[0].Key, item?.DeepClone());
                var result = await ExecuteSingleLlmCallAsync(step, perItemInputs, issues, cancellationToken, suppressIssues: true);
                if (result is not null)
                    rawResults.Add(result);
            }

            if (rawResults.Count == 0)
            {
                issues.Add(CreateIssue("mapped_llm_all_items_failed", $"Runtime LLM step '{step.Id}' failed for every mapped input item."));
                return new JsonObject();
            }

            return NormalizeMappedOutput(step, rawResults);
        }

        var inputsJson = BuildLlmInputs(resolvedInputs, null, null);
        var dataNode = await ExecuteSingleLlmCallAsync(step, inputsJson, issues, cancellationToken);
        if (issues.Count > 0)
            return new JsonObject();

        if (step.Out?.Format == RuntimeOutputFormats.String)
        {
            if (dataNode is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                issues.Add(CreateIssue("llm_string_output_invalid", $"Runtime step '{step.Id}' must return a JSON string."));
                return new JsonObject();
            }

            return new JsonObject
            {
                [step.Outputs[0].Name] = text
            };
        }

        _toolsById.TryGetValue(step.CapabilityId ?? string.Empty, out var tool);
        return NormalizeSingleOutput(step, tool, dataNode);
    }

    private async Task<JsonNode?> ExecuteSingleLlmCallAsync(
        RuntimeStep step,
        JsonObject inputsJson,
        List<PlanningIssue> issues,
        CancellationToken cancellationToken,
        bool suppressIssues = false)
    {
        ResultEnvelope<JsonElement?> envelope;
        var userPrompt = BuildLlmUserPrompt(step, inputsJson);
        var systemPrompt = BuildLlmSystemPrompt(step);

        try
        {
            _observer.OnEvent(new AgentPromptPreparedEvent(
                step.Id,
                $"runtime_{step.Id}",
                systemPrompt,
                userPrompt,
                userPrompt,
                PlanningNodeJson.ToElement(inputsJson)));
            envelope = await _llmClient.GenerateEnvelopeAsync(
                $"runtime_{step.Id}",
                systemPrompt,
                userPrompt,
                cancellationToken);
        }
        catch (Exception ex)
        {
            if (!suppressIssues)
                issues.Add(CreateIssue("llm_call_exception", $"Runtime step '{step.Id}' failed: {ex.Message}"));

            _observer.OnEvent(new AgentResponseReceivedEvent(
                step.Id,
                $"runtime_{step.Id}",
                string.Empty,
                false,
                null,
                new ErrorInfo("llm_call_exception", ex.Message)));
            return null;
        }

        _observer.OnEvent(new AgentResponseReceivedEvent(
            step.Id,
            $"runtime_{step.Id}",
            string.Empty,
            envelope.Ok,
            envelope.Data,
            envelope.Error));

        if (!envelope.Ok)
        {
            if (!suppressIssues)
            {
                issues.Add(CreateIssue(
                    envelope.Error?.Code ?? "llm_step_failed",
                    envelope.Error?.Message ?? $"Runtime step '{step.Id}' failed."));
            }

            return null;
        }

        var dataNode = envelope.Data is JsonElement element
            ? PlanningNodeJson.ToNode(element)
            : null;
        return dataNode;
    }

    private static string BuildLlmSystemPrompt(RuntimeStep step)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are executing one workflow step inside the production planning runtime.");
        sb.AppendLine("Use only the provided inputs. Do not invent facts.");
        sb.AppendLine("Return ONLY valid JSON with this exact top-level shape:");
        sb.AppendLine("{\"ok\":true|false,\"data\":...,\"error\":null|{\"code\":\"string\",\"message\":\"string\",\"details\":null}}");

        if (string.Equals(step.Out?.Format, RuntimeOutputFormats.String, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("When ok=true, data must be a JSON string.");
        }
        else if (step.Outputs.Count == 1)
        {
            var output = step.Outputs[0];
            sb.AppendLine($"When ok=true, data should be a JSON value suitable for output port '{output.Name}' with semantic type '{output.SemanticType}'.");
        }
        else
        {
            sb.AppendLine("When ok=true, data should be a JSON object containing the declared output ports.");
        }

        sb.AppendLine("When reliable completion is impossible, return ok=false with a short structured error.");
        return sb.ToString().Trim();
    }

    private static string BuildLlmUserPrompt(RuntimeStep step, JsonObject inputs) =>
        $$"""
        Step purpose:
        {{step.Purpose}}

        Step instruction:
        {{step.Instruction ?? step.Purpose}}

        Required outputs:
        {{PlanningNodeJson.SerializeIndented(step.Outputs)}}

        Inputs:
        {{inputs.ToJsonString(PlanningNodeJson.SerializerOptions)}}
        """;

    private static Dictionary<string, object?> BuildToolArguments(
        IReadOnlyDictionary<string, ResolvedInput> inputs,
        string? mappedInputName,
        JsonNode? mappedItem)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            var value = string.Equals(input.Key, mappedInputName, StringComparison.OrdinalIgnoreCase)
                ? mappedItem
                : input.Value.Value;
            result[input.Key] = ConvertNodeToObject(value);
        }

        return result;
    }

    private static JsonObject BuildLlmInputs(
        IReadOnlyDictionary<string, ResolvedInput> inputs,
        string? mappedInputName,
        JsonNode? mappedItem)
    {
        var result = new JsonObject();
        foreach (var input in inputs)
        {
            var value = string.Equals(input.Key, mappedInputName, StringComparison.OrdinalIgnoreCase)
                ? mappedItem
                : input.Value.Value;
            result[input.Key] = value?.DeepClone();
        }

        return result;
    }

    private static JsonObject NormalizeMappedOutput(RuntimeStep step, IReadOnlyList<JsonNode?> rawResults)
    {
        var result = new JsonObject();
        if (step.Outputs.Count == 1)
        {
            var array = new JsonArray(rawResults.Select(PlanningNodeJson.CloneNode).ToArray());
            result[step.Outputs[0].Name] = array;
            return result;
        }

        foreach (var output in step.Outputs)
            result[output.Name] = null;

        return result;
    }

    private static JsonObject NormalizeSingleOutput(RuntimeStep step, AppToolDescriptor? tool, JsonNode? rawResult)
    {
        var result = new JsonObject();
        if (step.Outputs.Count == 1)
        {
            var output = step.Outputs[0];
            if (rawResult is JsonObject rawObject && rawObject[output.Name] is JsonNode nested)
            {
                result[output.Name] = nested.DeepClone();
                return result;
            }

            if (rawResult is JsonObject toolObject
                && string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
                && RuntimeToolCapabilityMatcher.IsBuiltInWebSearch(tool, step.CapabilityId)
                && toolObject["results"] is JsonNode results)
            {
                result[output.Name] = results.DeepClone();
                return result;
            }

            result[output.Name] = rawResult?.DeepClone();
            return result;
        }

        if (rawResult is JsonObject objectResult)
        {
            foreach (var output in step.Outputs)
                result[output.Name] = objectResult[output.Name]?.DeepClone();
        }

        return result;
    }

    private static object? ConvertNodeToObject(JsonNode? node)
    {
        if (node is null)
            return null;

        var element = PlanningNodeJson.ToElement(node);
        return ConvertElementToObject(element);
    }

    private static object? ConvertElementToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => ConvertElementToObject(property.Value), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertElementToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };

    private static ResultEnvelope<JsonElement?> NormalizeToolResult(AppToolDescriptor tool, object? result)
    {
        if (result is null)
            return ResultEnvelope<JsonElement?>.Success(null);

        return result switch
        {
            CallToolResult callToolResult => NormalizeCallToolResult(tool, callToolResult),
            JsonElement element => ResultEnvelope<JsonElement?>.Success(element.Clone()),
            _ => ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(result))
        };
    }

    private static ResultEnvelope<JsonElement?> NormalizeCallToolResult(AppToolDescriptor tool, CallToolResult result)
    {
        if (result.IsError == true)
        {
            var message = ExtractCallToolText(result);
            var details = ExtractCallToolDetails(result);
            return ResultEnvelope<JsonElement?>.Failure(
                "tool_error",
                string.IsNullOrWhiteSpace(message)
                    ? $"Tool '{tool.QualifiedName}' returned an MCP error."
                    : message,
                details);
        }

        if (TryGetStructuredContent(result, out var structuredContent))
            return ResultEnvelope<JsonElement?>.Success(structuredContent);

        var text = ExtractCallToolText(result);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return TryParseJsonElement(text, out var jsonElement)
                ? ResultEnvelope<JsonElement?>.Success(jsonElement)
                : ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(text));
        }

        if (result.Content?.Count > 0)
            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(result.Content));

        return ResultEnvelope<JsonElement?>.Success(null);
    }

    private static bool TryGetStructuredContent(CallToolResult result, out JsonElement structuredContent)
    {
        structuredContent = default;

        if (result.StructuredContent is JsonNode node)
        {
            var element = JsonSerializer.SerializeToElement(node);
            if (element.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                structuredContent = element;
                return true;
            }
        }

        return false;
    }

    private static JsonElement? ExtractCallToolDetails(CallToolResult result)
    {
        if (TryGetStructuredContent(result, out var structuredContent))
            return structuredContent;

        if (result.Content?.Count > 0)
            return JsonSerializer.SerializeToElement(result.Content);

        return null;
    }

    private static string ExtractCallToolText(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
            return string.Empty;

        return string.Join(
            "\n",
            result.Content
                .OfType<TextContentBlock>()
                .Select(block => block.Text?.Trim())
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
    }

    private static bool TryParseJsonElement(string text, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static PlanningIssue CreateIssue(string code, string message) =>
        new()
        {
            Code = code,
            Message = message,
            Layer = "runtime_execution"
        };

    private sealed record ResolvedInput(JsonNode? Value, string Mode);
}
