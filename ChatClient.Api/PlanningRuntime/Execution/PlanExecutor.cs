using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;
using ChatClient.Api.Services;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        var outputContracts = DerivedStepOutputContractBuilder.Build(plan, toolCatalog.ListTools());
        ResultEnvelope<JsonElement?>? lastEnvelope = null;

        foreach (var step in plan.Steps)
        {
            if (IsReusable(step))
            {
                _log.Log($"[exec] step:reuse id={step.Id}");
                _observer.OnEvent(new StepReusedEvent(step.Id));
                traces.Add(CreateTrace(step, reused: true, calls: [], verificationIssues: []));
                continue;
            }

            var missingRefs = GetMissingRefs(step, stepMap);
            if (missingRefs.Count > 0)
                throw new InvalidOperationException($"Step '{step.Id}' is not ready. Missing resolved refs: {string.Join(", ", missingRefs)}");

            PlanExecutionState.ResetStep(step);

            var outputContract = outputContracts[step.Id];
            var (trace, envelope) = await ExecuteStepAsync(step, stepMap, outputContract, cancellationToken);
            traces.Add(trace);
            lastEnvelope = envelope;

            if (trace.Outcome == StepTraceOutcome.Failed)
                return new ExecutionResult { StepTraces = traces, LastEnvelope = lastEnvelope };
        }

        return new ExecutionResult { StepTraces = traces, LastEnvelope = lastEnvelope };
    }

    private async Task<(StepExecutionTrace trace, ResultEnvelope<JsonElement?> envelope)> ExecuteStepAsync(
        PlanStep step,
        IReadOnlyDictionary<string, PlanStep> stepMap,
        ResolvedPlanStepOutputContract outputContract,
        CancellationToken cancellationToken)
    {
        var calls = new List<JsonElement>();
        var outputs = new List<JsonElement?>();
        var partialFailures = new List<PartialCallFailure>();

        var (resolved, fanOutInputs) = ResolveInputs(step, stepMap);
        var resolvedInput = SerializeObject(resolved);
        var stepKind = PlanStepKinds.GetKind(step);
        var isTool = string.Equals(stepKind, PlanStepKinds.Tool, StringComparison.Ordinal);
        var toolMetadata = isTool
            ? toolCatalog.GetRequired(step.CapabilityId ?? throw new InvalidOperationException($"Tool step '{step.Id}' is missing capabilityId."))
            : null;
        var fanOutCount = fanOutInputs?.Values.FirstOrDefault()?.Length ?? 0;

        _log.Log($"[exec] step:start id={step.Id} kind={stepKind} capabilityId={PlanStepKinds.GetCapabilityId(step)} fanOut={(fanOutInputs is null ? "no" : fanOutCount.ToString())} mapped={outputContract.IsMapped.ToString().ToLowerInvariant()} contractSource={outputContract.Source} opaque={outputContract.IsOpaque.ToString().ToLowerInvariant()} resolvedInputs={SerializeElement(resolvedInput)}");
        _observer.OnEvent(new StepStartedEvent(
            step.Id,
            stepKind,
            PlanStepKinds.GetCapabilityId(step),
            resolvedInput.Clone(),
            fanOutInputs is null ? null : fanOutCount));

        ResultEnvelope<JsonElement?> envelope;
        if (fanOutInputs is null)
        {
            _log.Log($"[exec] call:start step={step.Id} callIndex=0 input={SerializeElement(resolvedInput)}");
            _observer.OnEvent(new StepCallStartedEvent(step.Id, 0, resolvedInput.Clone()));
            envelope = isTool
                ? await RunToolAsync(toolMetadata!, resolvedInput, calls, cancellationToken)
                : await RunAgentAsync(step, resolvedInput, outputContract, calls, cancellationToken);
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
                    : await RunAgentAsync(step, singleInput, outputContract, calls, cancellationToken);
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
                {
                    partialFailures.Add(new PartialCallFailure(
                        callIndex,
                        envelope.Error?.Code ?? "execution_failed",
                        envelope.Error?.Message ?? "Execution failed.",
                        CloneElement(envelope.Error?.Details)));
                    continue;
                }

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
                var trace = CreateTrace(step, false, calls, contractIssues);
                _observer.OnEvent(new StepCompletedEvent(trace, CloneElement(step.Result)));
                return (trace, envelope);
            }
        }

        if (!envelope.Ok && outputs.Count == 0)
        {
            step.Status = PlanStepStatuses.Fail;
            step.Error = CreatePlanStepError(envelope.Error);
            _log.Log($"[exec] step:end id={step.Id} success=False calls={calls.Count} error={Shorten(step.Error?.Message, 240)} details={SerializeElement(step.Error?.Details)}");
            var trace = CreateTrace(step, false, calls, []);
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
            var trace = CreateTrace(step, false, calls, verificationIssues);
            _observer.OnEvent(new StepCompletedEvent(trace, CloneElement(step.Result)));
            return (trace, envelope);
        }

        if (partialFailures.Count > 0)
        {
            step.Status = PlanStepStatuses.Partial;
            step.Error = CreatePartialFailureError(step.Id, fanOutCount, outputs.Count, partialFailures);
            var partialEnvelope = ResultEnvelope<JsonElement?>.Success(step.Result?.Clone());
            _log.Log($"[exec] step:stored id={step.Id} output={SerializeElement(step.Result)}");
            _log.Log($"[exec] step:end id={step.Id} success=True partial=True calls={calls.Count} error={Shorten(step.Error.Message, 240)} details={SerializeElement(step.Error.Details)}");
            var partialTrace = CreateTrace(step, false, calls, []);
            _observer.OnEvent(new StepCompletedEvent(partialTrace, CloneElement(step.Result)));
            return (partialTrace, partialEnvelope);
        }

        step.Status = PlanStepStatuses.Done;
        step.Error = null;
        _log.Log($"[exec] step:stored id={step.Id} output={SerializeElement(step.Result)}");
        _log.Log($"[exec] step:end id={step.Id} success=True calls={calls.Count} error=<none> details=null");
        var successTrace = CreateTrace(step, false, calls, []);
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

        ResultEnvelope<JsonElement?> envelope;
        try
        {
            using var interactionScope = mcpUserInteractionService?.BeginInteractionScope(McpInteractionScope.Planning);
            var result = await tool.ExecuteAsync(arguments, cancellationToken);
            envelope = NormalizeToolResult(tool, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            envelope = ResultEnvelope<JsonElement?>.Failure(
                "tool_error",
                ex.Message,
                JsonSerializer.SerializeToElement(new
                {
                    exception = ex.GetType().Name
                }));
        }

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
        ResolvedPlanStepOutputContract outputContract,
        List<JsonElement> calls,
        CancellationToken cancellationToken)
    {
        var inputFailure = ValidateNonToolInputContract(step, input);
        if (inputFailure is not null)
        {
            calls.Add(JsonSerializer.SerializeToElement(new
            {
                kind = PlanStepKinds.GetKind(step),
                capabilityId = PlanStepKinds.GetCapabilityId(step),
                ok = false,
                output = (JsonElement?)null,
                error = SerializeError(inputFailure.Error)
            }));

            return inputFailure;
        }

        var envelope = await agentStepRunner.ExecuteAsync(step, input, outputContract, cancellationToken);
        calls.Add(JsonSerializer.SerializeToElement(new
        {
            kind = PlanStepKinds.GetKind(step),
            capabilityId = PlanStepKinds.GetCapabilityId(step),
            ok = envelope.Ok,
            output = CloneElement(envelope.Data),
            error = SerializeError(envelope.Error)
        }));

        return envelope;
    }

    private static ResultEnvelope<JsonElement?>? ValidateNonToolInputContract(PlanStep step, JsonElement input)
    {
        var issues = new List<StepInputTypeIssue>();

        foreach (var entry in step.In)
        {
            if (!PlanInputBindingSyntax.TryParseBinding(entry.Value, out var binding, out var bindingError)
                || !string.IsNullOrWhiteSpace(bindingError)
                || binding is null
                || string.IsNullOrWhiteSpace(binding.Type))
            {
                continue;
            }

            if (!StepInputTypeValidator.TryParse(binding.Type, out var expectedType, out var typeError) || expectedType is null)
            {
                issues.Add(new StepInputTypeIssue(
                    "llm_input_type_invalid",
                    $"Input '{entry.Key}' declares invalid type '{binding.Type}'. {typeError}"));
                continue;
            }

            if (!input.TryGetProperty(entry.Key, out var resolvedValue))
            {
                issues.Add(new StepInputTypeIssue(
                    "llm_input_missing",
                    $"Resolved input is missing field '{entry.Key}'."));
                continue;
            }

            issues.AddRange(StepInputTypeValidator.ValidateResolvedValue(resolvedValue, expectedType, entry.Key));
        }

        if (issues.Count == 0)
            return null;

        var stepKind = PlanStepKinds.GetKind(step);
        var errorCode = string.Equals(stepKind, PlanStepKinds.Agent, StringComparison.Ordinal)
            ? "agent_input_contract_failed"
            : "llm_input_contract_failed";
        return ResultEnvelope<JsonElement?>.Failure(
            errorCode,
            $"{stepKind} step '{step.Id}' received input that does not match its declared input type hints.",
            JsonSerializer.SerializeToElement(new
            {
                issues = issues.Select(issue => new
                {
                    code = issue.Code,
                    message = issue.Message
                })
            }));
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

            if (PlanInputBindingSyntax.TryParseBinding(input.Value, out var bindingExpression, out var bindingError))
            {
                if (!string.IsNullOrWhiteSpace(bindingError))
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Id}': invalid binding for input '{input.Key}'. {bindingError}");
                }

                switch (bindingExpression)
                {
                    case PlanInputBindingSpec binding:
                        {
                            var resolution = PlanInputBindingSyntax.EvaluateReferenceOrThrow(binding.From, step.Id, stepMap);
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

                            break;
                        }
                    case PlanInputConcatBindingSpec concatBinding:
                        resolved[input.Key] = ResolveConcatBinding(step.Id, input.Key, concatBinding, stepMap);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Step '{step.Id}': input '{input.Key}' uses an unsupported binding expression.");
                }

                continue;
            }

            resolved[input.Key] = ConvertNodeToElement(input.Value);
        }

        if (fanOutInputs is not null)
            ValidateFanOutInputs(step, fanOutInputs);

        return (resolved, fanOutInputs);
    }

    private static JsonElement ResolveConcatBinding(
        string stepId,
        string inputName,
        PlanInputConcatBindingSpec concatBinding,
        IReadOnlyDictionary<string, PlanStep> stepMap)
    {
        if (concatBinding.Concat.Count == 0)
        {
            throw new InvalidOperationException(
                $"Step '{stepId}': input '{inputName}' uses concat but does not declare any sources.");
        }

        var flattened = new List<JsonElement>();
        foreach (var binding in concatBinding.Concat)
        {
            if (binding.Mode != PlanInputBindingMode.Value)
            {
                throw new InvalidOperationException(
                    $"Step '{stepId}': input '{inputName}' uses concat, but source '{binding.From}' does not use mode='value'.");
            }

            var resolution = PlanInputBindingSyntax.EvaluateReferenceOrThrow(binding.From, stepId, stepMap);
            if (resolution.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"Step '{stepId}': input '{inputName}' uses concat, but ref '{binding.From}' resolved to {DescribeValueKind(resolution)} instead of an array.");
            }

            flattened.AddRange(resolution.EnumerateArray().Select(item => item.Clone()));
        }

        return JsonSerializer.SerializeToElement(flattened.ToArray());
    }

    private static bool IsReusable(PlanStep step) =>
        PlanExecutionState.HasCompletedResult(step)
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
            $"Step '{step.Id}' returned data that does not match its derived output contract.",
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
        if (!outputContract.IsMapped)
            return outputs[0]!.Value.Clone();

        var logicalItems = new List<JsonElement>();
        foreach (var output in outputs)
        {
            if (output is not { } value)
                continue;

            if (value.ValueKind == JsonValueKind.Array)
            {
                logicalItems.AddRange(value.EnumerateArray().Select(item => item.Clone()));
                continue;
            }

            logicalItems.Add(value.Clone());
        }

        return JsonSerializer.SerializeToElement(logicalItems.ToArray());
    }

    private static PlanStepError CreateOutputContractError(
        string stepId,
        IReadOnlyCollection<StepVerificationIssue> issues,
        string scope) =>
        new()
        {
            Code = "output_contract_failed",
            Message = $"Step '{stepId}' produced {scope} output that does not match its derived contract.",
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
            if (!PlanInputBindingSyntax.TryParseBinding(value, out var bindingExpression, out var bindingError)
                || !string.IsNullOrWhiteSpace(bindingError)
                || bindingExpression is null)
                continue;

            foreach (var binding in PlanInputBindingSyntax.EnumerateBindings(bindingExpression))
            {
                if (!PlanInputBindingSyntax.TryParseReference(binding.From, out var reference, out _))
                    continue;

                if (!stepMap.TryGetValue(reference!.StepId, out var dependency)
                    || !PlanExecutionState.HasCompletedResult(dependency))
                {
                    missing.Add(binding.From);
                }
            }
        }

        return missing;
    }

    private static StepExecutionTrace CreateTrace(
        PlanStep step,
        bool reused,
        IEnumerable<JsonElement> calls,
        IReadOnlyCollection<StepVerificationIssue> verificationIssues) =>
        new()
        {
            StepId = step.Id,
            Outcome = ResolveTraceOutcome(step.Status),
            Reused = reused,
            ErrorCode = step.Error?.Code,
            ErrorMessage = step.Error?.Message,
            ErrorDetails = CloneElement(step.Error?.Details),
            Calls = calls.Select(call => call.Clone()).ToList(),
            VerificationIssues = verificationIssues
                .Select(issue => new StepVerificationIssue
                {
                    Code = issue.Code,
                    Message = issue.Message
                })
                .ToList()
        };

    private static StepTraceOutcome ResolveTraceOutcome(string? stepStatus) =>
        stepStatus switch
        {
            PlanStepStatuses.Done => StepTraceOutcome.Done,
            PlanStepStatuses.Partial => StepTraceOutcome.Partial,
            PlanStepStatuses.Fail => StepTraceOutcome.Failed,
            PlanStepStatuses.Skip => StepTraceOutcome.Skipped,
            _ => throw new InvalidOperationException(
                $"Step trace cannot be created for non-terminal status '{stepStatus ?? "<null>"}'.")
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

    private static PlanStepError CreatePartialFailureError(
        string stepId,
        int totalCalls,
        int successfulCalls,
        IReadOnlyList<PartialCallFailure> failures) =>
        new()
        {
            Code = "partial_failure",
            Message = $"Step '{stepId}' completed partially: {successfulCalls} of {totalCalls} calls succeeded.",
            Details = JsonSerializer.SerializeToElement(new
            {
                totalCalls,
                successfulCalls,
                failedCalls = failures.Count,
                failures = failures.Select(failure => new
                {
                    callIndex = failure.CallIndex,
                    code = failure.Code,
                    message = failure.Message,
                    details = CloneElement(failure.Details)
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
        PlanningLogFormatter.SummarizeElement(element);

    private static string Shorten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<none>";

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private sealed record PartialCallFailure(
        int CallIndex,
        string Code,
        string Message,
        JsonElement? Details);
}
