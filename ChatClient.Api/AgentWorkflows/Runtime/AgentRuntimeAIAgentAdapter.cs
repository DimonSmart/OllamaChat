using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.AgentRuntime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChatClient.Api.AgentWorkflows.Runtime;

public sealed class AgentRuntimeAIAgentAdapter(
    WorkflowRuntimeParticipant participant,
    ResolvedWorkflowParticipant? resolvedParticipant,
    AgentRuntimeCreationContext? creationContext,
    ChatClient.Application.Services.AgentRuntime.AgentRunContext? parentRunContext,
    IWorkflowParticipantInvoker participantInvoker) : AIAgent
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
        var invocationParticipant = resolvedParticipant ?? CreateResolvedParticipant(participant);
        var parentContext = parentRunContext ?? throw new InvalidOperationException(
            "Workflow participant adapter requires a parent run context.");
        var effectiveCreationContext = creationContext ?? new AgentRuntimeCreationContext
        {
            Configuration = new ChatClient.Domain.Models.AppChatConfiguration(string.Empty, [])
        };
        var invocation = participantInvoker.CreateInvocation(
            invocationParticipant,
            parentContext);
        var request = new AgentRuntimeRunRequest
        {
            Messages = messages
                .Select(ToInputMessage)
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .ToList()
        };

        AgentRunFailed? terminalFailure = null;
        var deliveredTextLengths = new Dictionary<string, int>(StringComparer.Ordinal);
        var responseId = invocation.Context.RunId;

        await foreach (var runEvent in participantInvoker.InvokeAsync(
                           invocation,
                           request,
                           effectiveCreationContext,
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
                        ResponseId = responseId
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
                        ResponseId = responseId,
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
            throw new AgentRunFailedException(terminalFailure.Error);
        }
    }

    private static ResolvedWorkflowParticipant CreateResolvedParticipant(
        WorkflowRuntimeParticipant participant)
    {
        if (participant.DefinitionReference is { } reference)
        {
            return new ResolvedWorkflowParticipant
            {
                ParticipantId = participant.Id,
                DisplayName = participant.DisplayName,
                Summary = participant.Summary,
                RuntimeKind = participant.Runtime.Descriptor.Kind,
                Source = new ReferencedParticipantSource(reference)
            };
        }

        return new ResolvedWorkflowParticipant
        {
            ParticipantId = participant.Id,
            DisplayName = participant.DisplayName,
            Summary = participant.Summary,
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            Source = new RuntimeWorkflowParticipantSource(participant.Runtime)
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
