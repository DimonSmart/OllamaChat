using Microsoft.Extensions.Logging;

namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentRunner(
    IAgentDefinitionExecutionDispatcher dispatcher) : IAgentRunner
{
    public IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentDefinitionReference reference,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext runContext,
        CancellationToken cancellationToken = default) =>
        dispatcher.ExecuteAsync(
            reference,
            request,
            creationContext,
            runContext,
            cancellationToken);
}

public sealed class AgentDefinitionExecutionDispatcher(
    IAgentDefinitionCatalog definitionCatalog,
    IAgentRunNestingValidator nestingValidator,
    IAgentRuntimeFactory runtimeFactory,
    IAgentRuntimeProtocolExecutor protocolExecutor,
    ILogger<AgentDefinitionExecutionDispatcher> logger) : IAgentDefinitionExecutionDispatcher
{
    public async IAsyncEnumerable<AgentRunEvent> ExecuteAsync(
        AgentDefinitionReference reference,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext runContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAgentRuntime? runtime = null;
        var startedAt = DateTimeOffset.UtcNow;
        AgentRunFailed? creationFailure = null;

        try
        {
            var descriptor = await definitionCatalog.GetRequiredAsync(reference, cancellationToken);
            var nestingValidation = nestingValidator.Validate(descriptor, runContext);
            if (!nestingValidation.IsValid)
            {
                logger.LogWarning(
                    "Agent run nesting validation failed. RunId={RunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, Code={Code}",
                    runContext.RunId,
                    reference.Kind,
                    reference.Id,
                    nestingValidation.Error?.Code);
                creationFailure = new AgentRunFailed(nestingValidation.Error!);
            }
            else
            {
                runtime = await runtimeFactory.CreateAsync(reference, creationContext, cancellationToken);
                logger.LogInformation(
                    "Agent run started. RunId={RunId}, ParentRunId={ParentRunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, DefinitionName={DefinitionName}, RuntimeKind={RuntimeKind}, NestingDepth={NestingDepth}, ConversationId={ConversationId}, StartedAt={StartedAt}",
                    runContext.RunId,
                    runContext.ParentRunId,
                    reference.Kind,
                    reference.Id,
                    descriptor.Name,
                    runtime.Descriptor.Kind,
                    GetWorkflowDepth(runContext),
                    runContext.ConversationId,
                    startedAt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Agent runtime creation failed. RunId={RunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}",
                runContext.RunId,
                reference.Kind,
                reference.Id);
            creationFailure = new AgentRunFailed(new AgentRunError(
                MapCreationFailureCode(reference, ex),
                BuildSafeCreationFailureMessage(reference, ex),
                false,
                ex));
        }

        if (creationFailure is not null)
        {
            yield return creationFailure;
            yield break;
        }

        if (runtime is null)
        {
            yield break;
        }

        AgentRunFailed? executionFailure = null;
        var terminalEmitted = false;
        var outcome = "Completed";
        var failureCode = (string?)null;
        var enumerator = protocolExecutor.RunAsync(
            runtime,
            request,
            runContext,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                AgentRunEvent runEvent;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    runEvent = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    outcome = "Canceled";
                    throw;
                }
                catch (AgentRuntimeProtocolException ex)
                {
                    outcome = "ProtocolViolation";
                    failureCode = "runtime_protocol_violation";
                    logger.LogError(
                        ex,
                        "Agent runtime protocol violation. RunId={RunId}, ParentRunId={ParentRunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}, NestingDepth={NestingDepth}, ConversationId={ConversationId}",
                        runContext.RunId,
                        runContext.ParentRunId,
                        reference.Kind,
                        reference.Id,
                        runtime.Descriptor.Kind,
                        runtime.Descriptor.Name,
                        GetWorkflowDepth(runContext),
                        runContext.ConversationId);
                    executionFailure = CreateProtocolFailure(ex);
                    break;
                }
                catch (AgentRunFailedException ex)
                {
                    outcome = "Failed";
                    failureCode = ex.Error.Code;
                    executionFailure = new AgentRunFailed(ex.Error);
                    break;
                }
                catch (Exception ex)
                {
                    outcome = "Failed";
                    failureCode = "runtime_execution_failed";
                    logger.LogError(
                        ex,
                        "Agent runtime execution failed. RunId={RunId}, ParentRunId={ParentRunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}, NestingDepth={NestingDepth}, ConversationId={ConversationId}",
                        runContext.RunId,
                        runContext.ParentRunId,
                        reference.Kind,
                        reference.Id,
                        runtime.Descriptor.Kind,
                        runtime.Descriptor.Name,
                        GetWorkflowDepth(runContext),
                        runContext.ConversationId);
                    executionFailure = CreateRuntimeExecutionFailure(ex);
                    break;
                }

                if (IsTerminal(runEvent))
                {
                    terminalEmitted = true;
                    if (runEvent is AgentRunFailed failed)
                    {
                        outcome = "Failed";
                        failureCode = failed.Error.Code;
                    }
                }

                yield return runEvent;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
                outcome = "Canceled";
                throw;
            }
            catch (Exception ex) when (executionFailure is null && !terminalEmitted)
            {
                outcome = "Failed";
                failureCode = "runtime_execution_failed";
                logger.LogError(
                    ex,
                    "Agent runtime disposal failed. RunId={RunId}, ParentRunId={ParentRunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}, NestingDepth={NestingDepth}, ConversationId={ConversationId}",
                    runContext.RunId,
                    runContext.ParentRunId,
                    reference.Kind,
                    reference.Id,
                    runtime.Descriptor.Kind,
                    runtime.Descriptor.Name,
                    GetWorkflowDepth(runContext),
                    runContext.ConversationId);
                executionFailure = CreateRuntimeExecutionFailure(ex);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Agent runtime disposal failed after terminal event. RunId={RunId}, ParentRunId={ParentRunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}",
                    runContext.RunId,
                    runContext.ParentRunId,
                    reference.Kind,
                    reference.Id,
                    runtime.Descriptor.Kind,
                    runtime.Descriptor.Name);
            }

            var completedAt = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Agent run finished. RunId={RunId}, ParentRunId={ParentRunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}, Outcome={Outcome}, FailureCode={FailureCode}, DurationMs={DurationMs}, Canceled={Canceled}, NestingDepth={NestingDepth}, ConversationId={ConversationId}, CompletedAt={CompletedAt}",
                runContext.RunId,
                runContext.ParentRunId,
                reference.Kind,
                reference.Id,
                runtime.Descriptor.Kind,
                runtime.Descriptor.Name,
                outcome,
                failureCode,
                (completedAt - startedAt).TotalMilliseconds,
                outcome == "Canceled" || cancellationToken.IsCancellationRequested,
                GetWorkflowDepth(runContext),
                runContext.ConversationId,
                completedAt);
        }

        if (executionFailure is not null &&
            !terminalEmitted)
        {
            yield return executionFailure;
        }
    }

    private static AgentRunFailed CreateProtocolFailure(AgentRuntimeProtocolException exception) =>
        new(new AgentRunError(
            "runtime_protocol_violation",
            "Agent runtime protocol violation.",
            false,
            exception));

    private static AgentRunFailed CreateRuntimeExecutionFailure(Exception exception) =>
        new(new AgentRunError(
            "runtime_execution_failed",
            "Agent runtime execution failed.",
            false,
            exception));

    private static bool IsTerminal(AgentRunEvent runEvent) =>
        runEvent is AgentRunCompleted or AgentRunFailed;

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

public sealed class AgentRuntimeProtocolExecutor(
    ILogger<AgentRuntimeProtocolExecutor> logger) : IAgentRuntimeProtocolExecutor
{
    public async IAsyncEnumerable<AgentRunEvent> RunAsync(
        IAgentRuntime runtime,
        AgentRuntimeRunRequest request,
        AgentRunContext runContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AgentRunEvent? pendingTerminal = null;
        await foreach (var runEvent in runtime.RunAsync(request, runContext, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            if (pendingTerminal is not null)
            {
                var isSecondTerminal = IsTerminal(runEvent);
                var message = isSecondTerminal
                    ? $"Runtime '{runtime.Descriptor.Name}' emitted more than one terminal event."
                    : $"Runtime '{runtime.Descriptor.Name}' emitted an event after a terminal event.";
                logger.LogError(
                    "Agent runtime protocol violation. RunId={RunId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}, EventType={EventType}, Violation={Violation}",
                    runContext.RunId,
                    runtime.Descriptor.Kind,
                    runtime.Descriptor.Name,
                    runEvent.GetType().Name,
                    isSecondTerminal ? "MultipleTerminalEvents" : "EventAfterTerminal");
                throw new AgentRuntimeProtocolException(message);
            }

            if (IsTerminal(runEvent))
            {
                pendingTerminal = runEvent;
                continue;
            }

            yield return runEvent;
        }

        if (pendingTerminal is null)
        {
            var message = $"Runtime '{runtime.Descriptor.Name}' completed without a terminal event.";
            logger.LogError(
                "Agent runtime protocol violation. RunId={RunId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}",
                runContext.RunId,
                runtime.Descriptor.Kind,
                runtime.Descriptor.Name);
            throw new AgentRuntimeProtocolException(message);
        }

        yield return pendingTerminal;
    }

    private static bool IsTerminal(AgentRunEvent runEvent) =>
        runEvent is AgentRunCompleted or AgentRunFailed;
}
