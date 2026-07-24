using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class OrchestrationWorkflowEventStreamProcessorTests
{
    [Fact]
    public async Task FinalizeActiveStreamsAsync_DoesNotCompleteEmptyStream()
    {
        var bridge = new AgenticChatEngineStreamingBridge();
        var processor = new OrchestrationWorkflowEventStreamProcessor(
            bridge,
            new HarnessResponseEventProjector(
                NullLogger<HarnessResponseEventProjector>.Instance));
        var stream = bridge.Create("host", "Host");
        var activeStreams = new Dictionary<Guid, StreamingAppChatMessage>
        {
            [stream.Id] = stream
        };
        var activeSpeakerIds = new Dictionary<Guid, string?>
        {
            [stream.Id] = "host"
        };
        var messages = new List<IAppChatMessage> { stream };
        var completedMessages = new List<OrchestrationCompletedAssistantMessage>();

        await processor.FinalizeActiveStreamsAsync(
            new OrchestrationWorkflowEventStreamContext
            {
                ModelName = "test-model",
                Workflow = null,
                Messages = messages,
                SpeakerIdsByMessageId = new Dictionary<Guid, string?>(),
                ActiveStreams = activeStreams,
                ActiveSpeakerIdsByStreamId = activeSpeakerIds,
                AgentIdsByExecutorId = new Dictionary<string, string>(),
                AgentIdsByName = new Dictionary<string, string>(),
                AgentNamesById = new Dictionary<string, string>
                {
                    ["host"] = "Host"
                },
                AddMessageAsync = message =>
                {
                    messages.Add(message);
                    return Task.CompletedTask;
                },
                ReplaceMessage = (source, replacement) =>
                {
                    var index = messages.IndexOf(source);
                    messages[index] = replacement;
                },
                NotifyMessageUpdatedAsync = (_, _) => Task.CompletedTask
            },
            completedMessages);

        Assert.Empty(completedMessages);
        Assert.Empty(activeStreams);
        Assert.Empty(activeSpeakerIds);
        var finalized = Assert.IsType<AppChatMessage>(Assert.Single(messages));
        Assert.Equal(string.Empty, finalized.Content);
        Assert.False(finalized.IsCanceled);
    }
}
