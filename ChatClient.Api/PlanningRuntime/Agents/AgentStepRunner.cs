using System.Text.Json;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Verification;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.PlanningRuntime.Agents;

public interface IAgentStepRunner
{
    Task<ResultEnvelope<JsonElement?>> ExecuteAsync(
        PlanStep step,
        JsonElement resolvedInputs,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes planner non-tool steps.
/// Ad-hoc LLM steps use prompts embedded in the plan step, while saved-agent steps invoke a
/// preconfigured callable agent from the planning catalog.
/// </summary>
public sealed class AgentStepRunner(IChatClient chatClient) : IAgentStepRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IPlanRunObserver _observer = NullPlanRunObserver.Instance;
    private readonly IAgenticExecutionInvoker? _agenticInvoker;
    private readonly PlanningCallableAgentCatalog _callableAgents = PlanningCallableAgentCatalog.Empty;

    public AgentStepRunner(IChatClient chatClient, IPlanRunObserver? planRunObserver = null) : this(chatClient)
    {
        _observer = planRunObserver ?? NullPlanRunObserver.Instance;
    }

    public AgentStepRunner(
        IChatClient chatClient,
        IAgenticExecutionInvoker agenticInvoker,
        PlanningCallableAgentCatalog callableAgents,
        IPlanRunObserver? planRunObserver = null) : this(chatClient)
    {
        _agenticInvoker = agenticInvoker ?? throw new ArgumentNullException(nameof(agenticInvoker));
        _callableAgents = callableAgents ?? throw new ArgumentNullException(nameof(callableAgents));
        _observer = planRunObserver ?? NullPlanRunObserver.Instance;
    }

    public async Task<ResultEnvelope<JsonElement?>> ExecuteAsync(
        PlanStep step,
        JsonElement resolvedInputs,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(step.Agent))
            return await ExecuteSavedAgentAsync(step, resolvedInputs, cancellationToken);

        return await ExecuteLlmAsync(step, resolvedInputs, cancellationToken);
    }

    private async Task<ResultEnvelope<JsonElement?>> ExecuteLlmAsync(
        PlanStep step,
        JsonElement resolvedInputs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(step.Llm))
            return ResultEnvelope<JsonElement?>.Failure("llm_missing", $"Step '{step.Id}' has no llm label.");
        if (string.IsNullOrWhiteSpace(step.SystemPrompt))
            return ResultEnvelope<JsonElement?>.Failure("llm_invalid_step", $"Step '{step.Id}' has no systemPrompt.");
        if (string.IsNullOrWhiteSpace(step.UserPrompt))
            return ResultEnvelope<JsonElement?>.Failure("llm_invalid_step", $"Step '{step.Id}' has no userPrompt.");

        var outputContract = PlanStepOutputContractResolver.Resolve(step, toolMetadata: null, hasFanOut: false);
        var systemPrompt = step.SystemPrompt;
        if (!systemPrompt.Contains("JSON", StringComparison.OrdinalIgnoreCase))
            systemPrompt += " Return ONLY valid JSON.";
        systemPrompt += BuildExecutionContract(outputContract);

        var fullUserPrompt = $"{step.UserPrompt}\n\nInput:\n{PlanningJson.SerializeIndented(new { inputs = resolvedInputs })}";
        _observer.OnEvent(new AgentPromptPreparedEvent(
            step.Id,
            step.Llm,
            systemPrompt,
            step.UserPrompt,
            fullUserPrompt,
            resolvedInputs.Clone()));
        var agent = new ChatClientAgent(chatClient, systemPrompt, step.Llm, null, null, null, null);

        try
        {
            var response = await agent.RunAsync<ResultEnvelope<JsonElement?>>(fullUserPrompt, null, JsonOptions, null, cancellationToken);
            var envelope = response.Result
                ?? throw new InvalidOperationException($"Step '{step.Id}' returned an empty response envelope.");
            var validatedEnvelope = ValidateEnvelope(step, outputContract, envelope);
            _observer.OnEvent(new AgentResponseReceivedEvent(
                step.Id,
                step.Llm,
                response.Text ?? string.Empty,
                validatedEnvelope.Ok,
                validatedEnvelope.Data?.Clone(),
                validatedEnvelope.Error is null
                    ? null
                    : new ErrorInfo(validatedEnvelope.Error.Code, validatedEnvelope.Error.Message, validatedEnvelope.Error.Details?.Clone())));

            return validatedEnvelope;
        }
        catch (Exception ex)
        {
            _observer.OnEvent(new AgentResponseReceivedEvent(
                step.Id,
                step.Llm,
                string.Empty,
                false,
                null,
                new ErrorInfo("llm_error", ex.Message)));
            return ResultEnvelope<JsonElement?>.Failure("llm_error", ex.Message);
        }
    }

    private async Task<ResultEnvelope<JsonElement?>> ExecuteSavedAgentAsync(
        PlanStep step,
        JsonElement resolvedInputs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.Agent))
            return ResultEnvelope<JsonElement?>.Failure("agent_missing", $"Step '{step.Id}' has no saved agent reference.");
        if (string.IsNullOrWhiteSpace(step.UserPrompt))
            return ResultEnvelope<JsonElement?>.Failure("agent_invalid_step", $"Step '{step.Id}' has no userPrompt.");
        if (_agenticInvoker is null)
        {
            return ResultEnvelope<JsonElement?>.Failure(
                "agent_runtime_missing",
                $"Saved-agent step '{step.Id}' cannot run because the planning agentic invoker is not configured.");
        }

        if (!_callableAgents.TryGet(step.Agent, out var callableAgent))
            return ResultEnvelope<JsonElement?>.Failure("agent_unknown", $"Step '{step.Id}' references unknown callable agent '{step.Agent}'.");

        var outputContract = PlanStepOutputContractResolver.Resolve(step, toolMetadata: null, hasFanOut: false);
        var fullUserPrompt = $"{step.UserPrompt}\n\nInput:\n{PlanningJson.SerializeIndented(new { inputs = resolvedInputs })}{BuildExecutionContract(outputContract)}";

        _observer.OnEvent(new AgentPromptPreparedEvent(
            step.Id,
            callableAgent.Agent.Agent.AgentName,
            callableAgent.Agent.Agent.Content,
            step.UserPrompt,
            fullUserPrompt,
            resolvedInputs.Clone()));

        try
        {
            var response = await _agenticInvoker.InvokeAsync(new AgenticExecutionRuntimeRequest
            {
                Agent = callableAgent.Agent.Agent,
                ResolvedModel = callableAgent.Agent.Model,
                Configuration = new ChatClient.Domain.Models.AppChatConfiguration(
                    callableAgent.Agent.Model.ModelName,
                    []),
                Conversation = [],
                UserMessage = fullUserPrompt
            }, cancellationToken);

            if (response.IsError)
            {
                _observer.OnEvent(new AgentResponseReceivedEvent(
                    step.Id,
                    callableAgent.Agent.Agent.AgentName,
                    response.FinalText,
                    false,
                    null,
                    new ErrorInfo("agent_error", response.ErrorMessage ?? "Agent execution failed.")));
                return ResultEnvelope<JsonElement?>.Failure("agent_error", response.ErrorMessage ?? "Agent execution failed.");
            }

            ResultEnvelope<JsonElement?> envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ResultEnvelope<JsonElement?>>(response.FinalText, JsonOptions)
                    ?? throw new InvalidOperationException($"Step '{step.Id}' returned an empty response envelope.");
            }
            catch (Exception ex)
            {
                _observer.OnEvent(new AgentResponseReceivedEvent(
                    step.Id,
                    callableAgent.Agent.Agent.AgentName,
                    response.FinalText,
                    false,
                    null,
                    new ErrorInfo("agent_invalid_contract", ex.Message)));
                return ResultEnvelope<JsonElement?>.Failure(
                    "agent_invalid_contract",
                    $"Saved-agent step '{step.Id}' did not return a valid JSON envelope. {ex.Message}");
            }

            var validatedEnvelope = ValidateEnvelope(step, outputContract, envelope);
            _observer.OnEvent(new AgentResponseReceivedEvent(
                step.Id,
                callableAgent.Agent.Agent.AgentName,
                response.FinalText,
                validatedEnvelope.Ok,
                validatedEnvelope.Data?.Clone(),
                validatedEnvelope.Error is null
                    ? null
                    : new ErrorInfo(validatedEnvelope.Error.Code, validatedEnvelope.Error.Message, validatedEnvelope.Error.Details?.Clone())));

            return validatedEnvelope;
        }
        catch (Exception ex)
        {
            _observer.OnEvent(new AgentResponseReceivedEvent(
                step.Id,
                callableAgent.Agent.Agent.AgentName,
                string.Empty,
                false,
                null,
                new ErrorInfo("agent_error", ex.Message)));
            return ResultEnvelope<JsonElement?>.Failure("agent_error", ex.Message);
        }
    }

    internal static string BuildExecutionContract(ResolvedPlanStepOutputContract outputContract)
    {
        var resultHint = string.Equals(outputContract.Format, PlanStepOutputFormats.String, StringComparison.OrdinalIgnoreCase)
            ? "a JSON string value"
            : "the requested JSON value";

        var aggregateHint = BuildAggregationHint(outputContract);
        var contractHint = BuildContractHint(outputContract);
        return $"\n\nAlways return ONLY valid JSON using this exact top-level shape: {{\"ok\":true|false,\"data\":{resultHint}|null,\"error\":null|{{\"code\":\"short_code\",\"message\":\"human readable message\",\"details\":{{\"status\":\"blocked|partial\",\"needsReplan\":true,\"type\":\"missing|error\",\"details\":[\"short detail\"]}}}}}}. If the task can be completed reliably, return ok=true, error=null, and put the full answer into data. If reliable completion is impossible, return ok=false, data=null, and fill error. Use status='blocked' when the requested entity or critical facts are absent. Use status='partial' when some useful context exists but the task is still incomplete. When ok=false, needsReplan must be true. Use type='missing' when critical input facts are absent. Use type='error' when the step is blocked by another execution problem. Put short factual details into details, such as missing field names, observed evidence, or concrete failure notes. Do not invent exact factual values that are not explicitly present in the provided inputs. If the task requires precise numbers, specs, dates, prices, names, or quotes and they are missing, return ok=false with a blocked or partial error instead of estimating. Do not return markdown or prose outside the JSON envelope.{aggregateHint}{contractHint}";
    }

    private static string BuildAggregationHint(ResolvedPlanStepOutputContract outputContract)
    {
        if (string.Equals(outputContract.Aggregate, PlanStepOutputAggregates.Collect, StringComparison.OrdinalIgnoreCase))
        {
            var hint = " Aggregation semantics for this call: return one result value for the CURRENT input. After all mapped calls finish, the runtime collects those per-call values into the final array. The schema below describes the single-call value, not the final collected array.";
            if (outputContract.CallSchema is { } callSchema
                && !PlanStepOutputContractResolver.SchemaDefinesArray(callSchema))
            {
                hint += " Do not wrap the value in an extra array.";
            }

            return hint;
        }

        if (string.Equals(outputContract.Aggregate, PlanStepOutputAggregates.Flatten, StringComparison.OrdinalIgnoreCase))
        {
            var hint = " Aggregation semantics for this call: return an array value for the CURRENT input. After all mapped calls finish, the runtime flattens the per-call arrays into the final array. The schema below describes the per-call array value that will be flattened.";
            if (outputContract.CallSchema is { } callSchema
                && PlanStepOutputContractResolver.SchemaDefinesArray(callSchema))
            {
                hint += " Do not collapse the array to a single object.";
            }

            return hint;
        }

        return string.Empty;
    }

    private static ResultEnvelope<JsonElement?> ValidateEnvelope(
        PlanStep step,
        ResolvedPlanStepOutputContract outputContract,
        ResultEnvelope<JsonElement?> envelope)
    {
        if (envelope.Ok)
        {
            if (envelope.Error is not null)
            {
                return ResultEnvelope<JsonElement?>.Failure(
                    "llm_invalid_contract",
                    $"Step '{step.Id}' returned ok=true with a non-null error payload.");
            }

            if (envelope.Data is null)
            {
                return ResultEnvelope<JsonElement?>.Failure(
                    "llm_invalid_contract",
                    $"Step '{step.Id}' returned ok=true with null data.");
            }

            if (string.Equals(outputContract.Format, PlanStepOutputFormats.String, StringComparison.OrdinalIgnoreCase))
            {
                if (envelope.Data is not { ValueKind: JsonValueKind.String })
                {
                    return ResultEnvelope<JsonElement?>.Failure(
                        "llm_invalid_contract",
                        $"Step '{step.Id}' expected string data in the response envelope.");
                }
            }

            var callIssues = StepOutputContractValidator.ValidateCallOutput(step.Id, outputContract, envelope.Data);
            if (callIssues.Count > 0)
            {
                return ResultEnvelope<JsonElement?>.Failure(
                    "llm_invalid_contract",
                    $"Step '{step.Id}' returned data that does not match its declared output contract.",
                    JsonSerializer.SerializeToElement(new
                    {
                        issues = callIssues.Select(issue => new
                        {
                            code = issue.Code,
                            message = issue.Message
                        })
                    }));
            }

            return ResultEnvelope<JsonElement?>.Success(envelope.Data.Value.Clone());
        }

        if (envelope.Error is null)
        {
            return ResultEnvelope<JsonElement?>.Failure(
                "llm_invalid_contract",
                $"Step '{step.Id}' returned ok=false without an error payload.");
        }

        if (string.IsNullOrWhiteSpace(envelope.Error.Message))
        {
            return ResultEnvelope<JsonElement?>.Failure(
                "llm_invalid_contract",
                $"Step '{step.Id}' returned ok=false with an empty error message.");
        }

        try
        {
            var validatedDetails = ValidateFailureDetails(step.Id, envelope.Error.Details);
            var errorCode = string.IsNullOrWhiteSpace(envelope.Error.Code) ? "llm_failed" : envelope.Error.Code.Trim();
            return ResultEnvelope<JsonElement?>.Failure(errorCode, envelope.Error.Message.Trim(), validatedDetails);
        }
        catch (Exception ex)
        {
            return ResultEnvelope<JsonElement?>.Failure("llm_invalid_contract", ex.Message);
        }
    }

    private static string BuildContractHint(ResolvedPlanStepOutputContract outputContract)
    {
        if (outputContract.CallSchema is null)
            return string.Empty;

        return $"\nExpected data contract for this call: format='{outputContract.Format}', aggregate='{outputContract.Aggregate}', schema={outputContract.CallSchema.Value.GetRawText()}.";
    }

    private static JsonElement ValidateFailureDetails(string stepId, JsonElement? details)
    {
        var typedDetails = details?.Deserialize<LlmFailureDetails>(JsonOptions)
            ?? throw new InvalidOperationException($"Step '{stepId}' returned ok=false without valid error.details.");

        if (string.IsNullOrWhiteSpace(typedDetails.Status))
            throw new InvalidOperationException($"Step '{stepId}' returned error.details without status.");

        if (!string.Equals(typedDetails.Status, "blocked", StringComparison.Ordinal)
            && !string.Equals(typedDetails.Status, "partial", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' returned error.details.status='{typedDetails.Status}', but only 'blocked' or 'partial' are allowed.");
        }

        if (!typedDetails.NeedsReplan)
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' returned ok=false with error.details.needsReplan=false.");
        }

        if (!string.Equals(typedDetails.Type, "missing", StringComparison.Ordinal)
            && !string.Equals(typedDetails.Type, "error", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' returned error.details.type='{typedDetails.Type}', but only 'missing' or 'error' are allowed.");
        }

        if (typedDetails.Details is null)
            throw new InvalidOperationException($"Step '{stepId}' returned error.details without details.");
        if (typedDetails.Details.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Step '{stepId}' returned error.details.details with blank items.");

        return details.Value.Clone();
    }
}
