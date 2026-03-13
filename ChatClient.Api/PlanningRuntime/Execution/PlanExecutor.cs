using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;

namespace ChatClient.Api.PlanningRuntime.Execution;

public sealed class PlanExecutor(
    IToolRegistry toolRegistry,
    IAgentStepRunner agentStepRunner,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null)
{
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;

    public async Task<ExecutionResult> ExecuteAsync(
        PlanDefinition plan,
        CancellationToken cancellationToken = default)
    {
        var traces = new List<StepExecutionTrace>();
        var stepMap = plan.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        ResultEnvelope<JsonElement?>? lastEnvelope = null;

        foreach (var step in plan.Steps)
        {
            if (IsReusable(step))
            {
                _log.Log($"[exec] step:reuse id={step.Id}");
                _observer.OnEvent(new StepReusedEvent(step.Id));
                traces.Add(new StepExecutionTrace
                {
                    StepId = step.Id,
                    Success = true,
                    Reused = true
                });
                continue;
            }

            var missingRefs = GetMissingRefs(step, stepMap);
            if (missingRefs.Count > 0)
                throw new InvalidOperationException($"Step '{step.Id}' is not ready. Missing resolved refs: {string.Join(", ", missingRefs)}");

            PlanExecutionState.ResetStep(step);

            var (trace, envelope) = await ExecuteStepAsync(step, stepMap, cancellationToken);
            traces.Add(trace);
            lastEnvelope = envelope;

            if (!trace.Success)
                return new ExecutionResult { StepTraces = traces, LastEnvelope = lastEnvelope };
        }

        return new ExecutionResult { StepTraces = traces, LastEnvelope = lastEnvelope };
    }

    private async Task<(StepExecutionTrace trace, ResultEnvelope<JsonElement?> envelope)> ExecuteStepAsync(
        PlanStep step,
        IReadOnlyDictionary<string, PlanStep> stepMap,
        CancellationToken cancellationToken)
    {
        var calls = new List<JsonElement>();
        var outputs = new List<JsonElement?>();

        var (resolved, fanOutInputs) = ResolveInputs(step, stepMap);
        var resolvedInput = SerializeObject(resolved);
        var isTool = !string.IsNullOrWhiteSpace(step.Tool);
        var fanOutCount = fanOutInputs?.Values.FirstOrDefault()?.Length ?? 0;

        _log.Log($"[exec] step:start id={step.Id} kind={(isTool ? "tool" : "llm")} name={(isTool ? step.Tool : step.Llm)} fanOut={(fanOutInputs is null ? "no" : fanOutCount.ToString())} resolvedInputs={SerializeElement(resolvedInput)}");
        _observer.OnEvent(new StepStartedEvent(
            step.Id,
            isTool ? "tool" : "llm",
            isTool ? step.Tool! : step.Llm!,
            resolvedInput.Clone(),
            fanOutInputs is null ? null : fanOutCount));

        ResultEnvelope<JsonElement?> envelope;
        if (fanOutInputs is null)
        {
            _log.Log($"[exec] call:start step={step.Id} callIndex=0 input={SerializeElement(resolvedInput)}");
            _observer.OnEvent(new StepCallStartedEvent(step.Id, 0, resolvedInput.Clone()));
            envelope = isTool
                ? await RunToolAsync(step.Tool!, resolvedInput, calls, cancellationToken)
                : await RunAgentAsync(step, resolvedInput, calls, cancellationToken);
            _log.Log($"[exec] call:end step={step.Id} callIndex=0 ok={envelope.Ok} output={SerializeElement(envelope.Data)} error={Shorten(envelope.Error?.Message, 240)} details={SerializeElement(envelope.Error?.Details)}");
            _observer.OnEvent(new StepCallCompletedEvent(
                step.Id,
                0,
                envelope.Ok,
                CloneElement(envelope.Data),
                envelope.Error is null
                    ? null
                    : new ErrorInfo(envelope.Error.Code, envelope.Error.Message, CloneElement(envelope.Error.Details))));

            if (envelope.Ok)
                outputs.Add(CloneElement(envelope.Data));
        }
        else
        {
            envelope = ResultEnvelope<JsonElement?>.Success(null);
            for (var callIndex = 0; callIndex < fanOutCount; callIndex++)
            {
                var singleInput = SubstituteScalars(resolved, fanOutInputs, callIndex);
                _log.Log($"[exec] call:start step={step.Id} callIndex={callIndex} input={SerializeElement(singleInput)}");
                _observer.OnEvent(new StepCallStartedEvent(step.Id, callIndex, singleInput.Clone()));
                envelope = isTool
                    ? await RunToolAsync(step.Tool!, singleInput, calls, cancellationToken)
                    : await RunAgentAsync(step, singleInput, calls, cancellationToken);
                _log.Log($"[exec] call:end step={step.Id} callIndex={callIndex} ok={envelope.Ok} output={SerializeElement(envelope.Data)} error={Shorten(envelope.Error?.Message, 240)} details={SerializeElement(envelope.Error?.Details)}");
                _observer.OnEvent(new StepCallCompletedEvent(
                    step.Id,
                    callIndex,
                    envelope.Ok,
                    CloneElement(envelope.Data),
                    envelope.Error is null
                        ? null
                        : new ErrorInfo(envelope.Error.Code, envelope.Error.Message, CloneElement(envelope.Error.Details))));

                if (!envelope.Ok)
                    break;

                outputs.Add(CloneElement(envelope.Data));
            }
        }

        if (outputs.Count > 0)
        {
            step.Result = fanOutInputs is null
                ? CloneElement(outputs[0])
                : JsonSerializer.SerializeToElement(outputs.Select(CloneElement).ToArray());
        }

        if (!envelope.Ok)
        {
            step.Status = PlanStepStatuses.Fail;
            step.Error = CreatePlanStepError(envelope.Error);
            _log.Log($"[exec] step:end id={step.Id} success=False calls={calls.Count} error={Shorten(step.Error?.Message, 240)} details={SerializeElement(step.Error?.Details)}");
            var trace = CreateTrace(step, success: false, reused: false, calls, []);
            _observer.OnEvent(new StepCompletedEvent(trace, CloneElement(step.Result)));
            return (trace, envelope);
        }

        var verificationIssues = StepOutputVerifier.Verify(step, step.Result);
        if (verificationIssues.Count > 0)
        {
            step.Status = PlanStepStatuses.Fail;
            step.Error = CreateVerificationError(step.Id, verificationIssues);
            foreach (var issue in verificationIssues)
                _log.Log($"[verify] step={step.Id} code={issue.Code} message={issue.Message}");

            envelope = ResultEnvelope<JsonElement?>.Failure(step.Error.Code, step.Error.Message, CloneElement(step.Error.Details));
            _log.Log($"[exec] step:end id={step.Id} success=False calls={calls.Count} error={Shorten(step.Error.Message, 240)} details={SerializeElement(step.Error.Details)}");
            var trace = CreateTrace(step, success: false, reused: false, calls, verificationIssues);
            _observer.OnEvent(new StepCompletedEvent(trace, CloneElement(step.Result)));
            return (trace, envelope);
        }

        step.Status = PlanStepStatuses.Done;
        step.Error = null;
        _log.Log($"[exec] step:stored id={step.Id} output={SerializeElement(step.Result)}");
        _log.Log($"[exec] step:end id={step.Id} success=True calls={calls.Count} error=<none> details=null");
        var successTrace = CreateTrace(step, success: true, reused: false, calls, []);
        _observer.OnEvent(new StepCompletedEvent(successTrace, CloneElement(step.Result)));
        return (successTrace, envelope);
    }

    private async Task<ResultEnvelope<JsonElement?>> RunToolAsync(
        string toolName,
        JsonElement input,
        List<JsonElement> calls,
        CancellationToken cancellationToken)
    {
        var tool = toolRegistry.GetRequired(toolName);
        var envelope = await tool.ExecuteAsync(input, cancellationToken);
        calls.Add(JsonSerializer.SerializeToElement(new
        {
            tool = toolName,
            input,
            ok = envelope.Ok,
            output = CloneElement(envelope.Data),
            error = SerializeError(envelope.Error)
        }));

        return envelope;
    }

    private async Task<ResultEnvelope<JsonElement?>> RunAgentAsync(
        PlanStep step,
        JsonElement input,
        List<JsonElement> calls,
        CancellationToken cancellationToken)
    {
        var envelope = await agentStepRunner.ExecuteAsync(step, input, cancellationToken);
        calls.Add(JsonSerializer.SerializeToElement(new
        {
            llm = step.Llm,
            ok = envelope.Ok,
            output = CloneElement(envelope.Data),
            error = SerializeError(envelope.Error)
        }));

        return envelope;
    }

    private (Dictionary<string, JsonElement?> resolved, Dictionary<string, JsonElement?[]>? fanOutInputs) ResolveInputs(
        PlanStep step,
        IReadOnlyDictionary<string, PlanStep> stepMap)
    {
        var resolved = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        Dictionary<string, JsonElement?[]>? fanOutInputs = null;
        var isTool = !string.IsNullOrWhiteSpace(step.Tool);

        foreach (var input in step.In)
        {
            if (input.Value is JsonValue value
                && value.TryGetValue<string>(out var text)
                && text.StartsWith("$", StringComparison.Ordinal))
            {
                var resolution = ParseStepRef(text[1..], step.Id, stepMap);
                if (!resolution.SuppressAutoMap && resolution.Value is { ValueKind: JsonValueKind.Array } array)
                {
                    var expectsScalar = isTool
                        ? ToolExpectsScalar(step.Tool!, input.Key)
                        : step.Each;

                    if (expectsScalar)
                    {
                        var fanOutValues = isTool && TryAutoProjectToolScalarArray(array, input.Key, out var projectedArray)
                            ? projectedArray.EnumerateArray().Select(item => (JsonElement?)item.Clone()).ToArray()
                            : array.EnumerateArray().Select(item => (JsonElement?)item.Clone()).ToArray();
                        fanOutInputs ??= new Dictionary<string, JsonElement?[]>(StringComparer.Ordinal);
                        fanOutInputs[input.Key] = fanOutValues;
                        resolved[input.Key] = fanOutValues.Length > 0
                            ? JsonSerializer.SerializeToElement(fanOutValues.Select(CloneElement).ToArray())
                            : array.Clone();
                        continue;
                    }
                }

                resolved[input.Key] = CloneElement(resolution.Value);
                continue;
            }

            resolved[input.Key] = ConvertNodeToElement(input.Value);
        }

        if (fanOutInputs is not null)
            ValidateFanOutInputs(step, fanOutInputs);

        return (resolved, fanOutInputs);
    }

    private static bool IsReusable(PlanStep step) =>
        PlanExecutionState.IsDone(step)
        || string.Equals(step.Status, PlanStepStatuses.Skip, StringComparison.Ordinal);

    private sealed record RefResolution(JsonElement? Value, bool SuppressAutoMap);

    private static RefResolution ParseStepRef(
        string expression,
        string currentStepId,
        IReadOnlyDictionary<string, PlanStep> stepMap)
    {
        var stepId = expression;
        int? arrayIndex = null;
        var projectArray = false;
        string? fieldName = null;

        var bracketStart = expression.IndexOf('[');
        if (bracketStart >= 0)
        {
            stepId = expression[..bracketStart];
            var bracketEnd = expression.IndexOf(']', bracketStart);
            if (bracketEnd < 0)
                throw new InvalidOperationException($"Step '{currentStepId}': invalid ref '${expression}' - unclosed '['.");

            var indexToken = expression[(bracketStart + 1)..bracketEnd];
            if (indexToken.Length == 0)
            {
                projectArray = true;
            }
            else if (int.TryParse(indexToken, out var parsedIndex))
            {
                arrayIndex = parsedIndex;
            }
            else
            {
                throw new InvalidOperationException($"Step '{currentStepId}': invalid array index in '${expression}'.");
            }

            if (bracketEnd + 1 < expression.Length && expression[bracketEnd + 1] == '.')
                fieldName = expression[(bracketEnd + 2)..];
        }
        else
        {
            var dotIndex = expression.IndexOf('.');
            if (dotIndex >= 0)
            {
                stepId = expression[..dotIndex];
                fieldName = expression[(dotIndex + 1)..];
            }
        }

        if (!stepMap.TryGetValue(stepId, out var referencedStep))
            throw new InvalidOperationException($"Step '{currentStepId}': ref '${expression}' - step '{stepId}' not found.");

        if (!PlanExecutionState.IsDone(referencedStep) || referencedStep.Result is null)
            throw new InvalidOperationException($"Step '{currentStepId}': ref '${expression}' - step '{stepId}' has no completed result.");

        var node = referencedStep.Result;
        if (projectArray)
        {
            if (node is not { ValueKind: JsonValueKind.Array } projectedArray)
                throw new InvalidOperationException($"Step '{currentStepId}': ref '${expression}' - step '{stepId}' output is not an array.");

            if (fieldName is null)
                return new RefResolution(projectedArray.Clone(), SuppressAutoMap: false);

            return new RefResolution(ProjectArrayField(projectedArray, fieldName, currentStepId, expression), SuppressAutoMap: false);
        }

        if (arrayIndex.HasValue)
        {
            if (node is not { ValueKind: JsonValueKind.Array } indexedArray)
                throw new InvalidOperationException($"Step '{currentStepId}': ref '${expression}' - step '{stepId}' output is not an array.");

            var indexedItems = indexedArray.EnumerateArray().ToArray();
            if (arrayIndex.Value < 0 || arrayIndex.Value >= indexedItems.Length)
                throw new InvalidOperationException($"Step '{currentStepId}': ref '${expression}' - index {arrayIndex} out of range (count={indexedItems.Length}).");

            node = indexedItems[arrayIndex.Value].Clone();
        }

        if (fieldName is null)
            return new RefResolution(CloneElement(node), SuppressAutoMap: arrayIndex.HasValue);

        return node switch
        {
            { ValueKind: JsonValueKind.Object } obj when obj.TryGetProperty(fieldName, out var property) => new RefResolution(property.Clone(), SuppressAutoMap: arrayIndex.HasValue),
            { ValueKind: JsonValueKind.Object } => throw new InvalidOperationException($"Step '{currentStepId}': ref '${expression}' - field '{fieldName}' was not found."),
            { ValueKind: JsonValueKind.Array } array => new RefResolution(ProjectArrayField(array, fieldName, currentStepId, expression), SuppressAutoMap: false),
            _ => throw new InvalidOperationException($"Step '{currentStepId}': ref '${expression}' - cannot access field '{fieldName}' on non-object.")
        };
    }

    private bool ToolExpectsScalar(string toolName, string paramName)
    {
        var metadata = toolRegistry.GetRequired(toolName).PlannerMetadata;
        if (!metadata.InputSchema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is not JsonObject properties)
        {
            throw new InvalidOperationException(
                $"Tool '{toolName}' has invalid planner metadata. Input schema must define an object 'properties' map.");
        }

        if (!properties.TryGetPropertyValue(paramName, out var parameterNode) || parameterNode is not JsonObject parameter)
            throw new InvalidOperationException($"Tool '{toolName}' does not declare input '{paramName}' in planner metadata.");
        if (!parameter.TryGetPropertyValue("type", out var typeNode))
            throw new InvalidOperationException($"Tool '{toolName}' input '{paramName}' is missing a JSON schema 'type'.");
        if (typeNode is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var typeName))
        {
            throw new InvalidOperationException(
                $"Tool '{toolName}' input '{paramName}' has invalid planner metadata. JSON schema 'type' must be a string.");
        }

        return !string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement SubstituteScalars(
        IReadOnlyDictionary<string, JsonElement?> resolved,
        IReadOnlyDictionary<string, JsonElement?[]> fanOutInputs,
        int index)
    {
        var copy = new Dictionary<string, JsonElement?>(resolved, StringComparer.Ordinal);
        foreach (var fanOutInput in fanOutInputs)
            copy[fanOutInput.Key] = CloneElement(fanOutInput.Value[index]);

        return SerializeObject(copy);
    }

    private static void ValidateFanOutInputs(PlanStep step, IReadOnlyDictionary<string, JsonElement?[]> fanOutInputs)
    {
        var expectedCount = fanOutInputs.First().Value.Length;
        foreach (var fanOutInput in fanOutInputs)
        {
            if (fanOutInput.Value.Length != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Step '{step.Id}' resolves multiple array inputs with different lengths. Cannot zip fan-out '{fanOutInputs.First().Key}' ({expectedCount}) and '{fanOutInput.Key}' ({fanOutInput.Value.Length}).");
            }
        }
    }

    private static JsonElement ProjectArrayField(JsonElement array, string fieldName, string currentStepId, string expression)
    {
        var projected = new List<JsonElement?>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Step '{currentStepId}': ref '${expression}' - cannot access field '{fieldName}' on non-object array item.");
            }

            if (!item.TryGetProperty(fieldName, out var property))
            {
                throw new InvalidOperationException(
                    $"Step '{currentStepId}': ref '${expression}' - field '{fieldName}' was not found on one of the array items.");
            }

            projected.Add(property.Clone());
        }

        return JsonSerializer.SerializeToElement(projected);
    }

    private static bool TryAutoProjectToolScalarArray(JsonElement array, string fieldName, out JsonElement projected)
    {
        projected = default;
        if (array.ValueKind != JsonValueKind.Array)
            return false;

        var values = new List<JsonElement?>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(fieldName, out var property))
                return false;

            values.Add(property.Clone());
        }

        if (values.Count == 0)
            return false;

        projected = JsonSerializer.SerializeToElement(values);
        return true;
    }

    private static List<string> GetMissingRefs(PlanStep step, IReadOnlyDictionary<string, PlanStep> stepMap)
    {
        var missing = new List<string>();
        foreach (var value in step.In.Values)
        {
            if (value is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var text)
                || !text.StartsWith("$", StringComparison.Ordinal))
            {
                continue;
            }

            var stepId = ExtractBaseStepId(text[1..]);
            if (!stepMap.TryGetValue(stepId, out var dependency)
                || !PlanExecutionState.IsDone(dependency)
                || dependency.Result is null)
            {
                missing.Add(text);
            }
        }

        return missing;
    }

    private static string ExtractBaseStepId(string expression)
    {
        var bracketIndex = expression.IndexOf('[');
        var dotIndex = expression.IndexOf('.');
        if (bracketIndex >= 0 && dotIndex >= 0)
            return expression[..Math.Min(bracketIndex, dotIndex)];
        if (bracketIndex >= 0)
            return expression[..bracketIndex];
        if (dotIndex >= 0)
            return expression[..dotIndex];
        return expression;
    }

    private static StepExecutionTrace CreateTrace(
        PlanStep step,
        bool success,
        bool reused,
        IEnumerable<JsonElement> calls,
        IReadOnlyCollection<StepVerificationIssue> verificationIssues) =>
        new()
        {
            StepId = step.Id,
            Success = success,
            Reused = reused,
            ErrorCode = success ? null : step.Error?.Code,
            ErrorMessage = success ? null : step.Error?.Message,
            ErrorDetails = success ? null : CloneElement(step.Error?.Details),
            Calls = calls.Select(call => call.Clone()).ToList(),
            VerificationIssues = verificationIssues
                .Select(issue => new StepVerificationIssue
                {
                    Code = issue.Code,
                    Message = issue.Message
                })
                .ToList()
        };

    private static PlanStepError? CreatePlanStepError(ErrorInfo? error)
    {
        if (error is null)
            return null;

        return new PlanStepError
        {
            Code = error.Code,
            Message = error.Message,
            Details = CloneElement(error.Details)
        };
    }

    private static PlanStepError CreateVerificationError(string stepId, IReadOnlyCollection<StepVerificationIssue> verificationIssues) =>
        new()
        {
            Code = "verification_failed",
            Message = $"Step '{stepId}' produced output that failed verification.",
            Details = JsonSerializer.SerializeToElement(new
            {
                issues = verificationIssues.Select(issue => new
                {
                    code = issue.Code,
                    message = issue.Message
                })
            })
        };

    private static JsonElement? SerializeError(ErrorInfo? error) =>
        error is null ? null : JsonSerializer.SerializeToElement(error);

    private static JsonElement SerializeObject(IReadOnlyDictionary<string, JsonElement?> node) =>
        JsonSerializer.SerializeToElement(node);

    private static JsonElement? ConvertNodeToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);

    private static JsonElement? CloneElement(JsonElement? element) =>
        element?.Clone();

    private static string SerializeElement(JsonElement? element) =>
        element?.GetRawText() ?? "null";

    private static string Shorten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<none>";

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }
}

