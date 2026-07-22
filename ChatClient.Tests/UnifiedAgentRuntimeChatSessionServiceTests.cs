using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Runtime.CompilerServices;
using System.Text;

namespace ChatClient.Tests;

public sealed class UnifiedAgentRuntimeChatSessionServiceTests
{
    [Fact]
    public async Task DirectHarness_ReusesSessionForTwoTurnsAndResetStartsFreshConversation()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request);
        var firstConversationId = fixture.Service.Id;

        await fixture.Service.SendAsync("first");
        await fixture.Service.SendAsync("second", [
            new AppChatMessageFile("notes.txt", 5, "text/plain", Encoding.UTF8.GetBytes("notes")),
            new AppChatMessageFile("pixel.png", 3, "image/png", [1, 2, 3])
        ]);

        Assert.Equal(2, fixture.ChatClient.Requests.Count);
        Assert.Equal("first", CurrentUserText(fixture.ChatClient.Requests[0].Messages));
        Assert.Equal("secondnotes", CurrentUserText(fixture.ChatClient.Requests[1].Messages));
        Assert.Contains(
            fixture.ChatClient.Requests[1].Messages.SelectMany(static message => message.Contents),
            static content => content is DataContent data && data.MediaType == "image/png");
        Assert.Equal("test-model", fixture.ChatClient.Requests[1].Options?.ModelId);
        Assert.Equal(0.35f, fixture.ChatClient.Requests[1].Options?.Temperature);
        Assert.Equal(1.15, fixture.ChatClient.Requests[1].Options?.AdditionalProperties?["repeat_penalty"]);
        Assert.Contains(
            fixture.ChatClient.Requests[1].Messages,
            static message => message.Role == ChatRole.Assistant && message.Text == "answer-1");

        await fixture.Service.ResetAsync();

        Assert.NotEqual(firstConversationId, fixture.Service.Id);
        Assert.Empty(fixture.Service.Messages);
        await fixture.Service.StartAsync(fixture.Request);
        await fixture.Service.SendAsync("fresh");
        Assert.Equal("fresh", CurrentUserText(fixture.ChatClient.Requests[2].Messages));
        Assert.DoesNotContain(
            fixture.ChatClient.Requests[2].Messages,
            static message => message.Role == ChatRole.Assistant && message.Text == "answer-1");
    }

    [Fact]
    public async Task SendAsync_ProjectsParticipantStreamsByRuntimeMessageId()
    {
        var runner = new StubAgentRunner([
            new AgentTextDelta("m1", "Planner", "plan"),
            new AgentTextDelta("m2", "Writer", "draft"),
            new AgentMessageCompleted("m1", new AgentOutputMessage("Planner", "plan")),
            new AgentMessageCompleted("m2", new AgentOutputMessage("Writer", "draft")),
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Workflow", "summary"),
                FinalMessageId = "summary",
                Messages =
                [
                    new AgentOutputMessage("Planner", "plan"),
                    new AgentOutputMessage("Writer", "draft")
                ]
            })
        ]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());

        await service.SendAsync("go");

        var assistants = service.Messages
            .Where(static message => message.Role == AppChatRole.Assistant)
            .ToList();
        Assert.Equal(3, assistants.Count);
        Assert.Contains(assistants, message => message.AgentName == "Planner" && message.Content == "plan");
        Assert.Contains(assistants, message => message.AgentName == "Writer" && message.Content == "draft");
        Assert.Contains(assistants, message => message.AgentName == "Workflow" && message.Content == "summary");
    }

    [Fact]
    public async Task SendAsync_DoesNotDuplicateFinalMessageWhenFinalMessageIdReferencesCompletedOutput()
    {
        var runner = new StubAgentRunner([
            new AgentTextDelta("m1", "Agent", "answer"),
            new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "answer")),
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Agent", "answer"),
                FinalMessageId = "m1",
                Messages = [new AgentOutputMessage("Agent", "answer")]
            })
        ]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());

        await service.SendAsync("go");

        var assistant = Assert.Single(
            service.Messages,
            static message => message.Role == AppChatRole.Assistant);
        Assert.Equal("answer", assistant.Content);
        Assert.Equal("Agent", assistant.AgentName);
    }

    [Theory]
    [MemberData(nameof(CompletedContentCases))]
    public async Task SendAsync_CompletedMessageReplacesStreamWithSameRuntimeMessageId(
        IReadOnlyList<AgentRunEvent> messageEvents,
        string expectedContent)
    {
        var events = messageEvents
            .Concat([
                new AgentRunCompleted(new AgentRunResult
                {
                    FinalMessage = new AgentOutputMessage("Agent", expectedContent),
                    FinalMessageId = "m1",
                    Messages = [new AgentOutputMessage("Agent", expectedContent)]
                })
            ])
            .ToList();
        var service = CreateService(new StubAgentRunner(events));
        await service.StartAsync(CreateStartRequest());

        await service.SendAsync("go");

        var assistant = Assert.Single(
            service.Messages,
            static message => message.Role == AppChatRole.Assistant);
        Assert.Equal(expectedContent, assistant.Content);
        Assert.Equal("Agent", assistant.AgentName);
        Assert.False(assistant.IsStreaming);
    }

    [Fact]
    public async Task SendAsync_CompletedRunFinalizesRemainingStreams()
    {
        var service = CreateService(new StubAgentRunner([
            new AgentTextDelta("m1", "Agent", "answer"),
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Agent", "answer"),
                FinalMessageId = "m1",
                Messages = [new AgentOutputMessage("Agent", "answer")]
            })
        ]));
        await service.StartAsync(CreateStartRequest());

        await service.SendAsync("go");

        var assistant = Assert.Single(
            service.Messages,
            static message => message.Role == AppChatRole.Assistant);
        Assert.Equal("answer", assistant.Content);
        Assert.False(assistant.IsStreaming);
        Assert.False(service.IsAnswering);
    }

    [Fact]
    public async Task SendAsync_FailedRunCancelsStreamsAndAddsOneErrorMessage()
    {
        var service = CreateService(new StubAgentRunner([
            new AgentTextDelta("m1", "Agent", "partial"),
            new AgentRunFailed(new AgentRunError("execution_failed", "boom", true))
        ]));
        await service.StartAsync(CreateStartRequest());

        await service.SendAsync("go");

        var assistants = service.Messages
            .Where(static message => message.Role == AppChatRole.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Single(assistants, static message => message.IsCanceled && !message.IsStreaming);
        Assert.Single(assistants, static message => message.Content == "Agent runtime error: boom");
        Assert.False(service.IsAnswering);
    }

    [Fact]
    public async Task CancelAsync_CancelsStreamsWithoutGenericError()
    {
        var runner = new BlockingAgentRunner();
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());

        var sendTask = service.SendAsync("go");
        await runner.WaitUntilStreamingAsync();

        await service.CancelAsync();
        await sendTask;

        var assistant = Assert.Single(
            service.Messages,
            static message => message.Role == AppChatRole.Assistant);
        Assert.True(assistant.IsCanceled);
        Assert.False(assistant.IsStreaming);
        Assert.DoesNotContain(
            service.Messages,
            static message => message.Content.StartsWith("Agent runtime error:", StringComparison.Ordinal));
        Assert.False(service.IsAnswering);
        Assert.True(service.RequiresReset);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("must not continue"));

        var canceledConversationId = service.Id;
        await service.ResetAsync();
        Assert.NotEqual(canceledConversationId, service.Id);
        Assert.Empty(service.Messages);
        Assert.False(service.RequiresReset);
    }

    [Fact]
    public async Task SendAsync_ForwardsCurrentUserAttachmentsToRuntimeRequest()
    {
        var runner = new StubAgentRunner([
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Agent", "done"),
                FinalMessageId = "final",
                Messages = [new AgentOutputMessage("Agent", "done")]
            })
        ]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());
        var file = new AppChatMessageFile(
            "notes.md",
            7,
            "text/markdown",
            Encoding.UTF8.GetBytes("# Notes"));

        await service.SendAsync("go", [file]);

        var attachment = Assert.Single(runner.LastRequest!.Attachments);
        Assert.Equal("notes.md", attachment.Name);
        Assert.Equal("text/markdown", attachment.ContentType);
        Assert.Equal("# Notes", attachment.Content);
        Assert.Equal(file.Data, attachment.Data);
    }

    [Fact]
    public async Task SendAsync_ForwardsRuntimeInputsToRuntimeRequest()
    {
        var runner = new StubAgentRunner([
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Agent", "done"),
                FinalMessageId = "final",
                Messages = [new AgentOutputMessage("Agent", "done")]
            })
        ]);
        var service = CreateService(runner);
        var request = new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model", []),
            Agents = [],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "workflow"),
            RuntimeInputs = new Dictionary<string, string>
            {
                ["topic"] = "runtime design",
                ["strict"] = "True"
            }
        };
        await service.StartAsync(request);

        await service.SendAsync("go");

        Assert.Equal("runtime design", runner.LastRequest!.Inputs["topic"]);
        Assert.Equal("True", runner.LastRequest.Inputs["strict"]);
    }

    private static UnifiedAgentRuntimeChatSessionService CreateService(IAgentRunner runner) =>
        new(
            runner,
            new StubDefinitionCatalog(),
            new AgentRunContextFactory(),
            new AgenticChatEngineStreamingBridge(),
            NullLogger<UnifiedAgentRuntimeChatSessionService>.Instance,
            null!,
            null!,
            new HarnessResponseEventProjector(NullLogger<HarnessResponseEventProjector>.Instance));

    private static DirectFixture CreateDirectFixture()
    {
        var templateId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var template = new AgentTemplateDefinition
        {
            Id = templateId,
            AgentName = "Harness test agent",
            Content = "Answer deterministically.",
            Temperature = 0.35,
            RepeatPenalty = 1.15
        };
        var model = new ServerModel(serverId, "test-model");
        var chatClient = new RecordingChatClient();

        var templateService = new Mock<IAgentTemplateService>(MockBehavior.Strict);
        templateService.Setup(service => service.GetByIdAsync(templateId)).ReturnsAsync(template);
        var serverService = new Mock<ILlmServerConfigService>(MockBehavior.Strict);
        serverService.Setup(service => service.GetByIdAsync(serverId)).ReturnsAsync(new LlmServerConfig
        {
            Id = serverId,
            Name = "Test server"
        });
        var clientFactory = new Mock<ILlmChatClientFactory>(MockBehavior.Strict);
        clientFactory.Setup(factory => factory.CreateAsync(model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatClient);
        var capabilities = new Mock<IModelCapabilityService>(MockBehavior.Strict);
        capabilities.Setup(service => service.SupportsFunctionCallingAsync(model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var tools = new Mock<IAppToolCatalog>(MockBehavior.Strict);
        var interaction = new Mock<IMcpUserInteractionService>(MockBehavior.Strict);
        var rag = new Mock<IAgenticRagContextService>(MockBehavior.Strict);
        rag.Setup(service => service.TryBuildContextAsync(
                templateId,
                It.IsAny<string>(),
                serverId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticRagContextResult());

        var runtimeFactory = new AgenticRuntimeAgentFactory(
            serverService.Object,
            clientFactory.Object,
            capabilities.Object,
            tools.Object,
            interaction.Object,
            rag.Object,
            Options.Create(new AgenticToolInvocationPolicyOptions()),
            NullLogger<AgenticRuntimeAgentFactory>.Instance);
        var service = new UnifiedAgentRuntimeChatSessionService(
            new StubAgentRunner([]),
            new StubDefinitionCatalog(),
            new AgentRunContextFactory(),
            new AgenticChatEngineStreamingBridge(),
            NullLogger<UnifiedAgentRuntimeChatSessionService>.Instance,
            templateService.Object,
            runtimeFactory,
            new HarnessResponseEventProjector(NullLogger<HarnessResponseEventProjector>.Instance));
        var request = new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("test-model", []),
            Agents = [],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, templateId.ToString()),
            RuntimeDefaultModel = model
        };

        return new DirectFixture(service, request, chatClient);
    }

    private static string CurrentUserText(IReadOnlyList<ChatMessage> messages) =>
        string.Concat(messages.Last(static message => message.Role == ChatRole.User)
            .Contents.OfType<TextContent>().Select(static content => content.Text));

    private sealed record DirectFixture(
        UnifiedAgentRuntimeChatSessionService Service,
        ChatEngineSessionStartRequest Request,
        RecordingChatClient ChatClient);

    private sealed class RecordingChatClient : IChatClient
    {
        public List<RecordedChatRequest> Requests { get; } = [];

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("test", null, "test-model")
                : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedChatRequest(messages.Select(static message => message.Clone()).ToList(), options));
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"answer-{Requests.Count}");
        }
    }

    private sealed record RecordedChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

    private static ChatEngineSessionStartRequest CreateStartRequest() =>
        new()
        {
            Configuration = new AppChatConfiguration("model", []),
            Agents = [],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "agent")
        };

    public static TheoryData<IReadOnlyList<AgentRunEvent>, string> CompletedContentCases()
    {
        var data = new TheoryData<IReadOnlyList<AgentRunEvent>, string>
        {
            {
                [
                    new AgentTextDelta("m1", "Agent", "answer"),
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "answer"))
                ],
                "answer"
            },
            {
                [
                    new AgentTextDelta("m1", "Agent", "answer "),
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "answer"))
                ],
                "answer"
            },
            {
                [
                    new AgentTextDelta("m1", "Agent", "partial"),
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "final answer"))
                ],
                "final answer"
            },
            {
                [
                    new AgentTextDelta("m1", "Agent", "final"),
                    new AgentTextDelta("m1", "Agent", " answer "),
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "final answer"))
                ],
                "final answer"
            },
            {
                [
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "answer"))
                ],
                "answer"
            }
        };

        return data;
    }

    private sealed class StubAgentRunner(IReadOnlyList<AgentRunEvent> events) : IAgentRunner
    {
        public AgentRuntimeRunRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentDefinitionReference reference,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AgentRunContext runContext,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            foreach (var runEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return runEvent;
            }
        }
    }

    private sealed class BlockingAgentRunner : IAgentRunner
    {
        private readonly TaskCompletionSource _streaming =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStreamingAsync() => _streaming.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentDefinitionReference reference,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AgentRunContext runContext,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentTextDelta("m1", "Agent", "partial");
            _streaming.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class StubDefinitionCatalog : IAgentDefinitionCatalog
    {
        public Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentDefinitionDescriptor>>([]);

        public Task<AgentDefinitionDescriptor?> FindAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentDefinitionDescriptor?>(new AgentDefinitionDescriptor
            {
                Reference = reference,
                Name = "Agent",
                RuntimeKind = reference.Kind == AgentDefinitionKind.SavedWorkflow
                    ? AgentRuntimeKind.WorkflowAgent
                    : AgentRuntimeKind.LlmAgent,
                ModelRequirement = AgentModelRequirement.Required
            });

        public async Task<AgentDefinitionDescriptor> GetRequiredAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            await FindAsync(reference, cancellationToken) ?? throw new KeyNotFoundException();
    }
}
