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
                "runtime_creation_failed",
                ex.Message,
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
        try
        {
            await foreach (var runEvent in runtime.RunAsync(request, runContext, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                lastEvent = runEvent;
                yield return runEvent;
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
}
