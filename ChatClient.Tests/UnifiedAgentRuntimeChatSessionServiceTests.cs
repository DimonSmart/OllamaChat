using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;

namespace ChatClient.Tests;

public sealed class UnifiedAgentRuntimeChatSessionServiceTests
{
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
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "agent"),
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
            NullLogger<UnifiedAgentRuntimeChatSessionService>.Instance);

    private static ChatEngineSessionStartRequest CreateStartRequest() =>
        new()
        {
            Configuration = new AppChatConfiguration("model", []),
            Agents = [],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "agent")
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
