using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;
using ChatClient.Api.Services;
using ModelContextProtocol.Protocol;

namespace ChatClient.Api.PlanningRuntime.Execution;

public sealed class PlanExecutor(
    PlanningToolCatalog toolCatalog,
    IAgentStepRunner agentStepRunner,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null,
    IMcpUserInteractionService? mcpUserInteractionService = null)
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
        var toolMetadata = isTool ? toolCatalog.GetRequired(step.Tool!) : null;
        var fanOutCount = fanOutInputs?.Values.FirstOrDefault()?.Length ?? 0;
        var outputContract = PlanStepOutputContractResolver.Resolve(step, toolMetadata, fanOutInputs is not null);

        _log.Log($"[exec] step:start id={step.Id} kind={(isTool ? "tool" : "llm")} name={(isTool ? step.Tool : step.Llm)} fanOut={(fanOutInputs is null ? "no" : fanOutCount.ToString())} aggregate={outputContract.Aggregate} resolvedInputs={SerializeElement(resolvedInput)}");
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
                ? await RunToolAsync(toolMetadata!, resolvedInput, calls, cancellationToken)
                : await RunAgentAsync(step, resolvedInput, calls, cancellationToken);
            if (envelope.Ok)
                envelope = ValidateCallOutput(step, outputContract, envelope);
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
                    ? await RunToolAsync(toolMetadata!, singleInput, calls, cancellationToken)
                    : await RunAgentAsync(step, singleInput, calls, cancellationToken);
                if (envelope.Ok)
                    envelope = ValidateCallOutput(step, outputContract, envelope);
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
            step.Result = BuildStepResult(step, outputContract, outputs);
            var contractIssues = StepOutputContractValidator.ValidateFinalOutput(step.Id, outputContract, step.Result);
            if (contractIssues.Count > 0)
            {
                step.Status = PlanStepStatuses.Fail;
                step.Error = CreateOutputContractError(step.Id, contractIssues, "final");
                envelope = ResultEnvelope<JsonElement?>.Failure(step.Error.Code, step.Error.Message, CloneElement(step.Error.Details));
                _log.Log($"[exec] step:end id={step.Id} success=False calls={calls.Count} error={Shorten(step.Error.Message, 240)} details={SerializeElement(step.Error.Details)}");
                var trace = CreateTrace(step, success: false, reused: false, calls, contractIssues);
                _observer.OnEvent(new StepCompletedEvent(trace, CloneElement(step.Result)));
                return (trace, envelope);
            }
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
        AppToolDescriptor tool,
        JsonElement input,
        List<JsonElement> calls,
        CancellationToken cancellationToken)
    {
        var inputIssues = ToolInputSchemaValidator.ValidateResolvedInput(input, tool.InputSchema);
        if (inputIssues.Count > 0)
        {
            var failure = ResultEnvelope<JsonElement?>.Failure(
                "input_contract_failed",
                $"Tool '{tool.QualifiedName}' received input that does not match its input schema.",
                JsonSerializer.SerializeToElement(new
                {
                    issues = inputIssues.Select(issue => new
                    {
                        code = issue.Code,
                        message = issue.Message
                    })
                }));

            calls.Add(JsonSerializer.SerializeToElement(new
            {
                tool = tool.QualifiedName,
                input,
                ok = false,
                output = (JsonElement?)null,
                error = SerializeError(failure.Error)
            }));

            return failure;
        }

        var arguments = ConvertInputToArguments(input);

        using var interactionScope = mcpUserInteractionService?.BeginInteractionScope(McpInteractionScope.Planning);
        var result = await tool.ExecuteAsync(arguments, cancellationToken);
        var envelope = NormalizeToolResult(tool, result);

        calls.Add(JsonSerializer.SerializeToElement(new
        {
            tool = tool.QualifiedName,
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

        foreach (var input in step.In)
        {
            if (PlanInputBindingSyntax.TryGetLegacyStringReference(input.Value, out var legacyReference))
            {
                throw new InvalidOperationException(
                    $"Step '{step.Id}': input '{input.Key}' uses legacy string ref syntax '{legacyReference}'. Use a binding object like {{\"from\":\"{legacyReference}\",\"mode\":\"value\"}}.");
            }

            if (PlanInputBindingSyntax.TryParseBinding(input.Value, out var binding, out var bindingError))
            {
                if (!string.IsNullOrWhiteSpace(bindingError))
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Id}': invalid binding for input '{input.Key}'. {bindingError}");
                }

                var resolution = PlanInputBindingSyntax.EvaluateReferenceOrThrow(binding!.From, step.Id, stepMap);
                resolved[input.Key] = resolution.Clone();

                if (binding.Mode == PlanInputBindingMode.Map)
                {
                    if (resolution.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException(
                            $"Step '{step.Id}': input '{input.Key}' uses mode='map' but ref '{binding.From}' did not resolve to an array.");
                    }

                    fanOutInputs ??= new Dictionary<string, JsonElement?[]>(StringComparer.Ordinal);
                    fanOutInputs[input.Key] = resolution.EnumerateArray().Select(item => (JsonElement?)item.Clone()).ToArray();
                }

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

    private static ResultEnvelope<JsonElement?> ValidateCallOutput(
        PlanStep step,
        ResolvedPlanStepOutputContract outputContract,
        ResultEnvelope<JsonElement?> envelope)
    {
        var issues = StepOutputContractValidator.ValidateCallOutput(step.Id, outputContract, envelope.Data);
        if (issues.Count == 0)
            return envelope;

        return ResultEnvelope<JsonElement?>.Failure(
            "output_contract_failed",
            $"Step '{step.Id}' returned data that does not match its declared output contract.",
            JsonSerializer.SerializeToElement(new
            {
                scope = "call",
                issues = issues.Select(issue => new
                {
                    code = issue.Code,
                    message = issue.Message
                })
            }));
    }

    private static JsonElement BuildStepResult(
        PlanStep step,
        ResolvedPlanStepOutputContract outputContract,
        IReadOnlyList<JsonElement?> outputs)
    {
        if (outputContract.Aggregate == PlanStepOutputAggregates.Single)
            return outputs[0]!.Value.Clone();

        if (outputContract.Aggregate == PlanStepOutputAggregates.Collect)
            return JsonSerializer.SerializeToElement(outputs.Select(CloneElement).ToArray());

        if (outputContract.Aggregate == PlanStepOutputAggregates.Flatten)
        {
            var flattened = new List<JsonElement>();
            for (var index = 0; index < outputs.Count; index++)
            {
                var output = outputs[index];
                if (output is not { ValueKind: JsonValueKind.Array } arrayOutput)
                    throw new InvalidOperationException(
                        $"Step '{step.Id}' uses out.aggregate='flatten' but call {index} returned {DescribeValueKind(output)} instead of an array.");

                flattened.AddRange(arrayOutput.EnumerateArray().Select(item => item.Clone()));
            }

            return JsonSerializer.SerializeToElement(flattened.ToArray());
        }

        throw new InvalidOperationException($"Step '{step.Id}' has unsupported out.aggregate='{outputContract.Aggregate}'.");
    }

    private static PlanStepError CreateOutputContractError(
        string stepId,
        IReadOnlyCollection<StepVerificationIssue> issues,
        string scope) =>
        new()
        {
            Code = "output_contract_failed",
            Message = $"Step '{stepId}' produced {scope} output that does not match its declared contract.",
            Details = JsonSerializer.SerializeToElement(new
            {
                scope,
                issues = issues.Select(issue => new
                {
                    code = issue.Code,
                    message = issue.Message
                })
            })
        };

    private static List<string> GetMissingRefs(PlanStep step, IReadOnlyDictionary<string, PlanStep> stepMap)
    {
        var missing = new List<string>();
        foreach (var value in step.In.Values)
        {
            if (!PlanInputBindingSyntax.TryParseBinding(value, out var binding, out var bindingError)
                || !string.IsNullOrWhiteSpace(bindingError)
                || !PlanInputBindingSyntax.TryParseReference(binding!.From, out var reference, out _))
                continue;

            if (!stepMap.TryGetValue(reference!.StepId, out var dependency)
                || !PlanExecutionState.IsDone(dependency)
                || dependency.Result is null)
            {
                missing.Add(binding.From);
            }
        }

        return missing;
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

    private static string DescribeValueKind(JsonElement? value) =>
        value switch
        {
            null => "null",
            { ValueKind: JsonValueKind.True } => "boolean",
            { ValueKind: JsonValueKind.False } => "boolean",
            { ValueKind: JsonValueKind.Number } => "number",
            { ValueKind: JsonValueKind.Null } => "null",
            { ValueKind: JsonValueKind.Undefined } => "null",
            { } element => element.ValueKind.ToString().ToLowerInvariant()
        };

    private static JsonElement SerializeObject(IReadOnlyDictionary<string, JsonElement?> node) =>
        JsonSerializer.SerializeToElement(node);

    private static Dictionary<string, object?> ConvertInputToArguments(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Tool input must resolve to a JSON object.");

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
            result[property.Name] = ConvertElementToArgumentValue(property.Value);

        return result;
    }

    private static object? ConvertElementToArgumentValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => ConvertElementToArgumentValue(property.Value),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertElementToArgumentValue)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => JsonSerializer.Deserialize<object?>(element.GetRawText())
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

    private static JsonElement? ConvertNodeToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);

    private static JsonElement? CloneElement(JsonElement? element) =>
        element?.Clone();

    private static string SerializeElement(JsonElement? element) =>
        PlanningJson.SerializeElementCompact(element);

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
