using Microsoft.Extensions.Logging;

namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentRunner(
    IAgentDefinitionCatalog definitionCatalog,
    IAgentRunNestingValidator nestingValidator,
    IAgentRuntimeFactory runtimeFactory,
    IAgentRuntimeProtocolExecutor protocolExecutor,
    ILogger<AgentRunner> logger) : IAgentRunner
{
    public async IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentDefinitionReference reference,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext runContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAgentRuntime? runtime = null;
        var startedAt = DateTimeOffset.UtcNow;
        var outcome = AgentRunOutcome.Incomplete;
        string? failureCode = null;
        bool? failureRetryable = null;
        try
        {
            AgentRunFailed? creationFailure = null;
            try
            {
                var descriptor = await definitionCatalog.GetRequiredAsync(reference, cancellationToken);
                var nestingValidation = nestingValidator.Validate(descriptor, runContext);
                if (!nestingValidation.IsValid)
                {
                    logger.LogWarning(
                        "Agent run nesting validation failed. RunId={RunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, Code={Code}",
                        runContext.RunId, reference.Kind, reference.Id, nestingValidation.Error?.Code);
                    creationFailure = new AgentRunFailed(nestingValidation.Error!);
                }
                else
                {
                    runtime = await runtimeFactory.CreateAsync(reference, creationContext, cancellationToken);
                    logger.LogInformation(
                        "Agent run started. RunId={RunId}, ParentRunId={ParentRunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, DefinitionName={DefinitionName}, RuntimeKind={RuntimeKind}, NestingDepth={NestingDepth}, ConversationId={ConversationId}, StartedAt={StartedAt}",
                        runContext.RunId, runContext.ParentRunId, reference.Kind, reference.Id, descriptor.Name,
                        runtime.Descriptor.Kind, GetWorkflowDepth(runContext), runContext.ConversationId, startedAt);
                }
            }
            catch (OperationCanceledException)
            {
                outcome = AgentRunOutcome.Canceled;
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent runtime creation failed. RunId={RunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}", runContext.RunId, reference.Kind, reference.Id);
                creationFailure = new AgentRunFailed(new AgentRunError(
                    MapCreationFailureCode(reference, ex), BuildSafeCreationFailureMessage(reference, ex), false, ex));
            }

            if (creationFailure is not null)
            {
                SetFailureOutcome(creationFailure, ref outcome, ref failureCode, ref failureRetryable);
                yield return creationFailure;
                yield break;
            }

            if (runtime is null)
            {
                yield break;
            }

            await using var enumerator = protocolExecutor.ExecuteAsync(
                runtime,
                request,
                runContext,
                cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (await MoveNextAsync(enumerator, () => outcome = AgentRunOutcome.Canceled))
            {
                var runEvent = enumerator.Current;
                switch (runEvent)
                {
                    case AgentRunCompleted:
                        outcome = AgentRunOutcome.Completed;
                        break;
                    case AgentRunFailed failed:
                        SetFailureOutcome(failed, ref outcome, ref failureCode, ref failureRetryable);
                        break;
                }

                yield return runEvent;
            }
        }
        finally
        {
            if (outcome == AgentRunOutcome.Incomplete && cancellationToken.IsCancellationRequested)
            {
                outcome = AgentRunOutcome.Canceled;
            }

            var completedAt = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Agent run finished. RunId={RunId}, ParentRunId={ParentRunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}, Outcome={Outcome}, FailureCode={FailureCode}, FailureRetryable={FailureRetryable}, DurationMs={DurationMs}, Canceled={Canceled}, NestingDepth={NestingDepth}, ConversationId={ConversationId}, CompletedAt={CompletedAt}",
                runContext.RunId,
                runContext.ParentRunId,
                reference.Kind,
                reference.Id,
                runtime?.Descriptor.Kind,
                runtime?.Descriptor.Name,
                outcome.ToString(),
                failureCode,
                failureRetryable,
                (completedAt - startedAt).TotalMilliseconds,
                cancellationToken.IsCancellationRequested,
                GetWorkflowDepth(runContext),
                runContext.ConversationId,
                completedAt);
        }
    }

    private static void SetFailureOutcome(
        AgentRunFailed failed,
        ref AgentRunOutcome outcome,
        ref string? failureCode,
        ref bool? failureRetryable)
    {
        outcome = string.Equals(failed.Error.Code, "runtime_protocol_violation", StringComparison.Ordinal)
            ? AgentRunOutcome.ProtocolViolation
            : AgentRunOutcome.Failed;
        failureCode = failed.Error.Code;
        failureRetryable = failed.Error.IsRetryable;
    }

    private static async Task<bool> MoveNextAsync(
        IAsyncEnumerator<AgentRunEvent> enumerator,
        Action markCanceled)
    {
        try
        {
            return await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            markCanceled();
            throw;
        }
    }

    private static int GetWorkflowDepth(AgentRunContext context)
        => context.DefinitionStack.Count(static frame =>
            frame.Definition.Kind == AgentDefinitionKind.SavedWorkflow);

    private static string MapCreationFailureCode(
        AgentDefinitionReference reference,
        Exception exception)
    {
        if (exception is KeyNotFoundException)
        {
            return reference.Kind == AgentDefinitionKind.SavedWorkflow
                ? "workflow_not_found"
                : "agent_not_found";
        }

        if (exception is InvalidOperationException &&
            exception.Message.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return "model_resolution_failed";
        }

        if (reference.Kind == AgentDefinitionKind.SavedWorkflow &&
            exception.Message.Contains("compil", StringComparison.OrdinalIgnoreCase))
        {
            return "workflow_compile_failed";
        }

        return "runtime_creation_failed";
    }

    private static string BuildSafeCreationFailureMessage(
        AgentDefinitionReference reference,
        Exception exception)
    {
        var code = MapCreationFailureCode(reference, exception);
        return code switch
        {
            "agent_not_found" => "Saved agent was not found.",
            "workflow_not_found" => "Saved workflow was not found.",
            "model_resolution_failed" => "Agent model selection could not be resolved.",
            "workflow_compile_failed" => "Workflow could not be compiled.",
            _ => "Agent runtime could not be created."
        };
    }
}

internal enum AgentRunOutcome
{
    Completed,
    Failed,
    Canceled,
    ProtocolViolation,
    Incomplete
}

public sealed class AgentRuntimeProtocolExecutor(
    ILogger<AgentRuntimeProtocolExecutor> logger) : IAgentRuntimeProtocolExecutor
{
    public async IAsyncEnumerable<AgentRunEvent> ExecuteAsync(
        IAgentRuntime runtime,
        AgentRuntimeRunRequest request,
        AgentRunContext runContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AgentRunEvent? pendingTerminal = null;
        AgentRunFailed? failure = null;
        var enumerator = runtime.RunAsync(request, runContext, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                AgentRunEvent runEvent;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                    runEvent = enumerator.Current;
                }
                catch (OperationCanceledException) { throw; }
                catch (AgentRuntimeProtocolException ex) { failure = ProtocolFailure(ex); break; }
                catch (AgentRunFailedException ex) { failure = new AgentRunFailed(ex.Error); break; }
                catch (Exception ex) { failure = ExecutionFailure(ex); break; }

                if (pendingTerminal is not null)
                {
                    failure = ProtocolFailure(new AgentRuntimeProtocolException(
                        IsTerminal(runEvent)
                            ? $"Runtime '{runtime.Descriptor.Name}' emitted more than one terminal event."
                            : $"Runtime '{runtime.Descriptor.Name}' emitted an event after a terminal event."));
                    break;
                }

                if (IsTerminal(runEvent))
                    pendingTerminal = runEvent;
                else
                    yield return runEvent;
            }
        }
        finally
        {
            try
            { await enumerator.DisposeAsync(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (failure is null && pendingTerminal is null)
            {
                failure = ExecutionFailure(ex);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Agent runtime disposal failed after terminal event. RunId={RunId}, RuntimeName={RuntimeName}",
                    runContext.RunId,
                    runtime.Descriptor.Name);
            }
        }

        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        if (pendingTerminal is null)
        {
            yield return ProtocolFailure(new AgentRuntimeProtocolException(
                $"Runtime '{runtime.Descriptor.Name}' completed without a terminal event."));
            yield break;
        }

        yield return pendingTerminal;
    }

    private static AgentRunFailed ProtocolFailure(Exception exception) =>
        new(new AgentRunError("runtime_protocol_violation", "Agent runtime protocol violation.", false, exception));

    private static AgentRunFailed ExecutionFailure(Exception exception) =>
        new(new AgentRunError("runtime_execution_failed", "Agent runtime execution failed.", false, exception));

    private static bool IsTerminal(AgentRunEvent runEvent) =>
        runEvent is AgentRunCompleted or AgentRunFailed;
}
