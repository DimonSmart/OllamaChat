using Microsoft.Extensions.Logging;

namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentRunner(
    IAgentRuntimeFactory runtimeFactory,
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
        AgentRunFailed? creationFailure = null;

        try
        {
            runtime = await runtimeFactory.CreateAsync(reference, creationContext, cancellationToken);
            logger.LogInformation(
                "Agent run started. RunId={RunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, RuntimeKind={RuntimeKind}, Name={Name}, StartedAt={StartedAt}",
                runContext.RunId,
                reference.Kind,
                reference.Id,
                runtime.Descriptor.Kind,
                runtime.Descriptor.Name,
                startedAt);
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

        AgentRunEvent? lastEvent = null;
        var terminalEventSeen = false;
        try
        {
            await foreach (var runEvent in runtime.RunAsync(request, runContext, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (terminalEventSeen)
                {
                    var message = $"Runtime '{runtime.Descriptor.Name}' emitted an event after a terminal event.";
                    logger.LogError(
                        "Agent runtime protocol violation. RunId={RunId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}, EventType={EventType}",
                        runContext.RunId,
                        runtime.Descriptor.Kind,
                        runtime.Descriptor.Name,
                        runEvent.GetType().Name);
                    throw new AgentRuntimeProtocolException(message);
                }

                if (IsTerminal(runEvent))
                {
                    terminalEventSeen = true;
                }

                lastEvent = runEvent;
                yield return runEvent;
            }

            if (!terminalEventSeen)
            {
                var message = $"Runtime '{runtime.Descriptor.Name}' completed without a terminal event.";
                logger.LogError(
                    "Agent runtime protocol violation. RunId={RunId}, RuntimeKind={RuntimeKind}, RuntimeName={RuntimeName}",
                    runContext.RunId,
                    runtime.Descriptor.Kind,
                    runtime.Descriptor.Name);
                throw new AgentRuntimeProtocolException(message);
            }
        }
        finally
        {
            var completedAt = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Agent run finished. RunId={RunId}, DefinitionKind={DefinitionKind}, DefinitionId={DefinitionId}, RuntimeKind={RuntimeKind}, Name={Name}, CompletedAt={CompletedAt}, Canceled={Canceled}, FailureCode={FailureCode}",
                runContext.RunId,
                reference.Kind,
                reference.Id,
                runtime.Descriptor.Kind,
                runtime.Descriptor.Name,
                completedAt,
                cancellationToken.IsCancellationRequested,
                lastEvent is AgentRunFailed failed ? failed.Error.Code : null);
        }
    }

    private static bool IsTerminal(AgentRunEvent runEvent) =>
        runEvent is AgentRunCompleted or AgentRunFailed;

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
