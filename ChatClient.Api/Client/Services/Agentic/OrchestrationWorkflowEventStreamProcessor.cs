using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
#pragma warning disable MAAI001
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
#pragma warning restore MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class OrchestrationWorkflowEventStreamProcessor(
    IChatEngineStreamingBridge streamingBridge)
{
    internal IReadOnlyList<OrchestrationDeliveredAssistantMessage> CreateDeliveredAssistantMessagesSnapshot(
        OrchestrationWorkflowEventStreamContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<OrchestrationDeliveredAssistantMessage> deliveredMessages = [];

        foreach (var message in context.Messages)
        {
            if (message.IsStreaming || message.Role != AppChatRole.Assistant)
            {
                continue;
            }

            var content = message.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            context.SpeakerIdsByMessageId.TryGetValue(message.Id, out var speakerIdFromMessageMap);
            var speakerId = !string.IsNullOrWhiteSpace(message.AgentId)
                ? message.AgentId
                : speakerIdFromMessageMap;
            var speakerName = !string.IsNullOrWhiteSpace(message.AgentName)
                ? message.AgentName
                : ResolveSpeakerName(context, speakerId);

            deliveredMessages.Add(new OrchestrationDeliveredAssistantMessage(content, speakerId, speakerName));
        }

        return deliveredMessages;
    }

    internal async Task<bool> DrainAsync(
        IAsyncEnumerable<WorkflowEvent> workflowEvents,
        OrchestrationWorkflowEventStreamContext context,
        OrchestrationWorkflowPassStreamingState streamingState,
        IReadOnlyList<OrchestrationDeliveredAssistantMessage> deliveredAssistantMessages,
        List<OrchestrationCompletedAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflowEvents);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(streamingState);
        ArgumentNullException.ThrowIfNull(deliveredAssistantMessages);
        ArgumentNullException.ThrowIfNull(completedAssistantMessages);

        var assistantOutputObserved = false;

        await foreach (var workflowEvent in workflowEvents.WithCancellation(cancellationToken))
        {
            assistantOutputObserved |= await ProcessWorkflowEventAsync(
                workflowEvent,
                context,
                streamingState,
                deliveredAssistantMessages,
                completedAssistantMessages,
                cancellationToken);
        }

        return assistantOutputObserved;
    }

    internal async Task FinalizeActiveStreamsAsync(
        OrchestrationWorkflowEventStreamContext context,
        List<OrchestrationCompletedAssistantMessage> completedAssistantMessages)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completedAssistantMessages);

        foreach (var stream in context.ActiveStreams.Values.ToList())
        {
            await FinalizeStreamAsync(
                context,
                stream,
                completedAssistantMessages);
        }

        context.ActiveStreams.Clear();
        context.ActiveSpeakerIdsByStreamId.Clear();
    }

    private async Task<bool> ProcessWorkflowEventAsync(
        WorkflowEvent workflowEvent,
        OrchestrationWorkflowEventStreamContext context,
        OrchestrationWorkflowPassStreamingState streamingState,
        IReadOnlyList<OrchestrationDeliveredAssistantMessage> deliveredAssistantMessages,
        List<OrchestrationCompletedAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (workflowEvent)
        {
            case AgentResponseUpdateEvent updateEvent:
                var updateText = ExtractUpdateText(updateEvent);
                if (string.IsNullOrWhiteSpace(updateText))
                {
                    return false;
                }

                var stream = await GetOrCreateStreamAsync(
                    context,
                    updateEvent.ExecutorId,
                    updateText,
                    streamingState,
                    completedAssistantMessages,
                    cancellationToken);

                streamingBridge.Append(stream, updateText);
                await context.NotifyMessageUpdatedAsync(stream, false);
                return true;

            case WorkflowOutputEvent outputEvent:
                var assistantMessages = ExtractOutputMessages(outputEvent)
                    .Where(static chatMessage => chatMessage.Role == ChatRole.Assistant)
                    .Where(static chatMessage => !string.IsNullOrWhiteSpace(chatMessage.Text))
                    .ToList();
                if (assistantMessages.Count == 0)
                {
                    return false;
                }

                foreach (var chatMessage in GetUndeliveredOutputMessages(
                             context,
                             assistantMessages,
                             deliveredAssistantMessages,
                             completedAssistantMessages))
                {
                    await PublishCompletedOutputMessageAsync(
                        context,
                        chatMessage,
                        completedAssistantMessages,
                        cancellationToken);
                }

                return true;

            case ExecutorFailedEvent failedEvent:
                await FinalizeActiveStreamsAsync(context, completedAssistantMessages);
                throw failedEvent.Data ?? new InvalidOperationException(
                    $"Workflow executor '{failedEvent.ExecutorId}' failed without an exception payload.");

            case RequestInfoEvent requestInfoEvent:
                await FinalizeActiveStreamsAsync(context, completedAssistantMessages);
                throw new InvalidOperationException(
                    $"Workflow requested unsupported external input: {requestInfoEvent.Request}");

            default:
                return false;
        }
    }

    private async Task PublishCompletedOutputMessageAsync(
        OrchestrationWorkflowEventStreamContext context,
        ChatMessage chatMessage,
        List<OrchestrationCompletedAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outputText = chatMessage.Text;
        if (string.IsNullOrWhiteSpace(outputText))
        {
            return;
        }

        var speakerId = ResolveSpeakerIdFromAuthorName(context, chatMessage.AuthorName);
        var speakerName = ResolveSpeakerName(context, speakerId) ?? chatMessage.AuthorName;
        var existing = context.ActiveStreams.Values.LastOrDefault();

        if (existing is not null)
        {
            context.ActiveSpeakerIdsByStreamId.TryGetValue(existing.Id, out var existingSpeakerId);
            if (IsSameSpeaker(existingSpeakerId, existing.AgentName, speakerId, speakerName))
            {
                UpdateStreamSpeaker(existing, speakerId, speakerName);
                if (!string.IsNullOrWhiteSpace(speakerId))
                {
                    context.ActiveSpeakerIdsByStreamId[existing.Id] = speakerId;
                }

                if (!string.Equals(existing.Content, outputText, StringComparison.Ordinal))
                {
                    existing.ResetContent();
                    existing.Append(outputText);
                }

                await FinalizeStreamAsync(context, existing, completedAssistantMessages);
                return;
            }

            await FinalizeStreamAsync(context, existing, completedAssistantMessages);
        }

        var finalMessage = new AppChatMessage(
            outputText,
            DateTime.Now,
            AppChatRole.Assistant,
            BuildStatistics(speakerName, context.ModelName),
            agentId: speakerId,
            agentName: speakerName);
        await context.AddMessageAsync(finalMessage);
        completedAssistantMessages.Add(new OrchestrationCompletedAssistantMessage(
            finalMessage,
            speakerId ?? speakerName));
    }

    private static IEnumerable<ChatMessage> GetUndeliveredOutputMessages(
        OrchestrationWorkflowEventStreamContext context,
        IReadOnlyList<ChatMessage> outputMessages,
        IReadOnlyList<OrchestrationDeliveredAssistantMessage> deliveredAssistantMessages,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> completedAssistantMessages)
    {
        var deliveredMessages = deliveredAssistantMessages
            .Concat(completedAssistantMessages.Select(static completedMessage =>
                new OrchestrationDeliveredAssistantMessage(
                    completedMessage.Message.Content ?? string.Empty,
                    completedMessage.SpeakerId ?? completedMessage.Message.AgentId,
                    completedMessage.Message.AgentName)))
            .ToList();
        var skipCount = FindDeliveredOutputOverlap(context, outputMessages, deliveredMessages);

        for (var index = skipCount; index < outputMessages.Count; index++)
        {
            yield return outputMessages[index];
        }
    }

    private static int FindDeliveredOutputOverlap(
        OrchestrationWorkflowEventStreamContext context,
        IReadOnlyList<ChatMessage> outputMessages,
        IReadOnlyList<OrchestrationDeliveredAssistantMessage> deliveredMessages)
    {
        var maxOverlap = Math.Min(outputMessages.Count, deliveredMessages.Count);
        for (var overlap = maxOverlap; overlap > 0; overlap--)
        {
            var deliveredStartIndex = deliveredMessages.Count - overlap;
            var matches = true;

            for (var index = 0; index < overlap; index++)
            {
                if (!IsSameDeliveredAssistantMessage(
                        context,
                        outputMessages[index],
                        deliveredMessages[deliveredStartIndex + index]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return overlap;
            }
        }

        return 0;
    }

    private static bool IsSameDeliveredAssistantMessage(
        OrchestrationWorkflowEventStreamContext context,
        ChatMessage outputMessage,
        OrchestrationDeliveredAssistantMessage deliveredMessage)
    {
        if (!string.Equals(
                outputMessage.Text?.Trim(),
                deliveredMessage.Content.Trim(),
                StringComparison.Ordinal))
        {
            return false;
        }

        var outputSpeakerId = ResolveSpeakerIdFromAuthorName(context, outputMessage.AuthorName);
        var outputSpeakerName = ResolveSpeakerName(context, outputSpeakerId) ?? outputMessage.AuthorName;

        return IsSameSpeaker(
            deliveredMessage.SpeakerId,
            deliveredMessage.SpeakerName,
            outputSpeakerId,
            outputSpeakerName);
    }

    private async Task<StreamingAppChatMessage> GetOrCreateStreamAsync(
        OrchestrationWorkflowEventStreamContext context,
        string? executorId,
        string outputText,
        OrchestrationWorkflowPassStreamingState streamingState,
        List<OrchestrationCompletedAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
    {
        var speakerId = WorkflowSpeakerResolver.ResolveSpeakerId(
            executorId,
            context.AgentIdsByExecutorId,
            context.Workflow,
            streamingState.NextAssistantMessageIndex);
        var speakerName = ResolveSpeakerName(context, speakerId);
        var existing = context.ActiveStreams.Values.LastOrDefault();
        if (existing is not null)
        {
            context.ActiveSpeakerIdsByStreamId.TryGetValue(existing.Id, out var existingSpeakerId);
            if (ShouldContinueCurrentStream(
                    existing,
                    existingSpeakerId,
                    speakerId,
                    speakerName,
                    outputText))
            {
                UpdateStreamSpeaker(existing, speakerId, speakerName);
                if (!string.IsNullOrWhiteSpace(speakerId))
                {
                    context.ActiveSpeakerIdsByStreamId[existing.Id] = speakerId;
                }

                return existing;
            }

            await FinalizeStreamAsync(context, existing, completedAssistantMessages);
        }

        var stream = streamingBridge.Create(speakerId, speakerName);
        context.ActiveStreams[stream.Id] = stream;
        context.ActiveSpeakerIdsByStreamId[stream.Id] = speakerId;
        streamingState.RegisterStartedAssistantMessage();
        await context.AddMessageAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return stream;
    }

    private async Task FinalizeStreamAsync(
        OrchestrationWorkflowEventStreamContext context,
        StreamingAppChatMessage stream,
        List<OrchestrationCompletedAssistantMessage> completedAssistantMessages)
    {
        context.ActiveSpeakerIdsByStreamId.TryGetValue(stream.Id, out var speakerId);

        var finalMessage = streamingBridge.Complete(
            stream,
            BuildStatistics(stream.AgentName, context.ModelName));

        context.ReplaceMessage(stream, finalMessage);
        context.ActiveStreams.Remove(stream.Id);
        context.ActiveSpeakerIdsByStreamId.Remove(stream.Id);

        var resolvedSpeakerId = speakerId;
        if (string.IsNullOrWhiteSpace(resolvedSpeakerId) &&
            !string.IsNullOrWhiteSpace(finalMessage.AgentName))
        {
            context.AgentIdsByName.TryGetValue(finalMessage.AgentName, out resolvedSpeakerId);
        }

        if (!string.IsNullOrWhiteSpace(resolvedSpeakerId) &&
            !string.Equals(finalMessage.AgentId, resolvedSpeakerId, StringComparison.OrdinalIgnoreCase))
        {
            finalMessage.AgentId = resolvedSpeakerId;
        }

        completedAssistantMessages.Add(new OrchestrationCompletedAssistantMessage(
            finalMessage,
            resolvedSpeakerId ?? finalMessage.AgentName));
        await context.NotifyMessageUpdatedAsync(finalMessage, true);
    }

    private static IEnumerable<ChatMessage> ExtractOutputMessages(WorkflowOutputEvent outputEvent)
    {
        if (outputEvent.Is<List<ChatMessage>>(out var listMessages) && listMessages is not null)
        {
            return listMessages;
        }

        if (outputEvent.Is<IReadOnlyList<ChatMessage>>(out var readOnlyMessages) && readOnlyMessages is not null)
        {
            return readOnlyMessages;
        }

        if (outputEvent.Is<ChatMessage>(out var singleMessage) && singleMessage is not null)
        {
            return [singleMessage];
        }

        if (outputEvent.Is<string>(out var stringMessage) && !string.IsNullOrWhiteSpace(stringMessage))
        {
            return [new ChatMessage(ChatRole.Assistant, stringMessage)];
        }

        return [];
    }

    private static string? ExtractUpdateText(AgentResponseUpdateEvent updateEvent)
    {
        if (!string.IsNullOrWhiteSpace(updateEvent.Update.Text))
        {
            return updateEvent.Update.Text;
        }

        return updateEvent.Update.ToString();
    }

    private static string? ResolveSpeakerName(
        OrchestrationWorkflowEventStreamContext context,
        string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            return null;
        }

        return context.AgentNamesById.TryGetValue(speakerId, out var speakerName)
            ? speakerName
            : speakerId;
    }

    private static string? ResolveSpeakerIdFromAuthorName(
        OrchestrationWorkflowEventStreamContext context,
        string? authorName)
    {
        if (string.IsNullOrWhiteSpace(authorName))
        {
            return null;
        }

        if (context.AgentIdsByName.TryGetValue(authorName, out var speakerId))
        {
            return speakerId;
        }

        return context.AgentIdsByExecutorId.TryGetValue(authorName, out speakerId)
            ? speakerId
            : null;
    }

    private static string BuildStatistics(string? agentName, string modelName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            return $"workflow orchestration | model {modelName}";
        }

        return $"workflow orchestration | speaker {agentName} | model {modelName}";
    }

    private static void UpdateStreamSpeaker(
        StreamingAppChatMessage stream,
        string? speakerId,
        string? speakerName)
    {
        if (!string.IsNullOrWhiteSpace(speakerId) &&
            !string.Equals(stream.AgentId, speakerId, StringComparison.Ordinal))
        {
            stream.SetAgentId(speakerId);
        }

        if (!string.IsNullOrWhiteSpace(speakerName) &&
            !string.Equals(stream.AgentName, speakerName, StringComparison.Ordinal))
        {
            stream.SetAgentName(speakerName);
        }
    }

    private static bool ShouldContinueCurrentStream(
        StreamingAppChatMessage existing,
        string? existingSpeakerId,
        string? nextSpeakerId,
        string? nextSpeakerName,
        string outputText)
    {
        if (!string.IsNullOrWhiteSpace(nextSpeakerId) ||
            !string.IsNullOrWhiteSpace(nextSpeakerName))
        {
            return IsSameSpeaker(
                existingSpeakerId,
                existing.AgentName,
                nextSpeakerId,
                nextSpeakerName);
        }

        return WorkflowStreamingTextDelta.IsDuplicateOfCurrentMessage(existing.Content, outputText);
    }

    private static bool IsSameSpeaker(
        string? existingSpeakerId,
        string? existingSpeakerName,
        string? nextSpeakerId,
        string? nextSpeakerName)
    {
        if (!string.IsNullOrWhiteSpace(existingSpeakerId) &&
            !string.IsNullOrWhiteSpace(nextSpeakerId))
        {
            return string.Equals(existingSpeakerId, nextSpeakerId, StringComparison.OrdinalIgnoreCase);
        }

        if ((!string.IsNullOrWhiteSpace(existingSpeakerId) ||
             !string.IsNullOrWhiteSpace(nextSpeakerId)) &&
            !string.IsNullOrWhiteSpace(existingSpeakerName) &&
            !string.IsNullOrWhiteSpace(nextSpeakerName))
        {
            return string.Equals(existingSpeakerName, nextSpeakerName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(existingSpeakerName, nextSpeakerName, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class OrchestrationWorkflowEventStreamContext
{
    public required string ModelName { get; init; }

    public required IOrchestrationWorkflowDefinition? Workflow { get; init; }

    public required IReadOnlyList<IAppChatMessage> Messages { get; init; }

    public required IReadOnlyDictionary<Guid, string?> SpeakerIdsByMessageId { get; init; }

    public required IDictionary<Guid, StreamingAppChatMessage> ActiveStreams { get; init; }

    public required IDictionary<Guid, string?> ActiveSpeakerIdsByStreamId { get; init; }

    public required IReadOnlyDictionary<string, string> AgentIdsByExecutorId { get; init; }

    public required IReadOnlyDictionary<string, string> AgentIdsByName { get; init; }

    public required IReadOnlyDictionary<string, string> AgentNamesById { get; init; }

    public required Func<IAppChatMessage, Task> AddMessageAsync { get; init; }

    public required Action<IAppChatMessage, IAppChatMessage> ReplaceMessage { get; init; }

    public required Func<IAppChatMessage, bool, Task> NotifyMessageUpdatedAsync { get; init; }
}

internal sealed record OrchestrationDeliveredAssistantMessage(
    string Content,
    string? SpeakerId,
    string? SpeakerName);

internal sealed class OrchestrationWorkflowPassStreamingState(int assistantMessagesBeforePass)
{
    private int _startedAssistantMessages;

    public int NextAssistantMessageIndex => assistantMessagesBeforePass + _startedAssistantMessages;

    public void RegisterStartedAssistantMessage()
    {
        _startedAssistantMessages++;
    }
}
