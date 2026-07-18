using ChatClient.Application.Services.AgentRuntime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChatClient.Api.AgentWorkflows.Runtime;

public sealed class AgentRuntimeAIAgentAdapter(
    WorkflowRuntimeParticipant participant,
    IAgentRunContextFactory contextFactory,
    IAgentRuntimeProtocolExecutor protocolExecutor) : AIAgent
{
    protected override string? IdCore => participant.Id;

    public override string? Name => participant.DisplayName;

    public override string? Description => participant.Summary;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new RuntimeAdapterAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, string>(), options));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new RuntimeAdapterAgentSession());

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var updates = RunCoreStreamingAsync(messages, session, options, cancellationToken);
        return await updates.ToAgentResponseAsync(cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parentContext = CreateParentContext();
        var childContext = contextFactory.CreateChild(
            parentContext,
            participant.DefinitionReference);
        var request = new AgentRuntimeRunRequest
        {
            Messages = messages
                .Select(ToInputMessage)
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .ToList()
        };

        AgentRunFailed? terminalFailure = null;
        var deliveredTextLengths = new Dictionary<string, int>(StringComparer.Ordinal);

        await foreach (var runEvent in protocolExecutor.RunAsync(
                           participant.Runtime,
                           request,
                           childContext,
                           cancellationToken))
        {
            switch (runEvent)
            {
                case AgentTextDelta delta:
                    deliveredTextLengths[delta.MessageId] =
                        deliveredTextLengths.GetValueOrDefault(delta.MessageId) + delta.Text.Length;
                    yield return new AgentResponseUpdate(ChatRole.Assistant, delta.Text)
                    {
                        AgentId = participant.Id,
                        AuthorName = participant.DisplayName,
                        MessageId = delta.MessageId,
                        ResponseId = childContext.RunId
                    };
                    break;

                case AgentMessageCompleted completed:
                    var deliveredLength = deliveredTextLengths.GetValueOrDefault(completed.MessageId);
                    var remainingText = completed.Message.Content.Length > deliveredLength
                        ? completed.Message.Content[deliveredLength..]
                        : string.Empty;
                    yield return new AgentResponseUpdate(ChatRole.Assistant, remainingText)
                    {
                        AgentId = participant.Id,
                        AuthorName = participant.DisplayName,
                        MessageId = completed.MessageId,
                        ResponseId = childContext.RunId,
                        FinishReason = ChatFinishReason.Stop
                    };
                    break;

                case AgentRunCompleted:
                    yield break;

                case AgentRunFailed failed:
                    terminalFailure = failed;
                    break;
            }
        }

        if (terminalFailure is not null)
        {
            throw new InvalidOperationException(
                $"Workflow participant '{participant.DisplayName}' failed: {terminalFailure.Error.Message}",
                terminalFailure.Error.Exception);
        }
    }

    private ChatClient.Application.Services.AgentRuntime.AgentRunContext CreateParentContext()
    {
        return new ChatClient.Application.Services.AgentRuntime.AgentRunContext
        {
            RunId = Guid.NewGuid().ToString("N")
        };
    }

    private static AgentInputMessage ToInputMessage(ChatMessage message) =>
        new(
            message.Role == ChatRole.System
                ? AgentMessageRole.System
                : message.Role == ChatRole.Assistant
                    ? AgentMessageRole.Assistant
                    : AgentMessageRole.User,
            message.Text ?? string.Empty);

    private sealed class RuntimeAdapterAgentSession : AgentSession;
}
