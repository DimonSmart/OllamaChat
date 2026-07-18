using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AppAgentRunContext = ChatClient.Application.Services.AgentRuntime.AgentRunContext;

namespace ChatClient.Tests;

public sealed class AgentRuntimeAIAgentAdapterTests
{
    [Fact]
    public async Task RunStreamingAsync_UsesChildRunIdAsResponseId()
    {
        var parentContext = CreateParentContext();
        var invoker = new StubParticipantInvoker([
            new AgentTextDelta("message-1", "Inner", "hello "),
            new AgentMessageCompleted("message-1", new AgentOutputMessage("Inner", "hello world")),
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Inner", "hello world"),
                FinalMessageId = "message-1"
            })
        ]);
        var adapter = CreateAdapter(parentContext, invoker);

        var updates = await CollectAsync(adapter.RunStreamingAsync("go"));

        Assert.NotNull(invoker.ParentContext);
        Assert.Equal(parentContext.RunId, invoker.ParentContext.RunId);
        Assert.All(updates, update => Assert.NotEqual(parentContext.RunId, update.ResponseId));
    }

    [Fact]
    public async Task RunStreamingAsync_ThrowsAgentRunFailedExceptionWithoutRewritingError()
    {
        var error = new AgentRunError(
            "workflow_cycle_detected",
            "cycle",
            false)
        {
            Metadata = new Dictionary<string, string>
            {
                ["participant.id"] = "inner",
                ["definition.stack"] = "outer > inner"
            }
        };
        var adapter = CreateAdapter(
            CreateParentContext(),
            new StubParticipantInvoker([new AgentRunFailed(error)]));

        var exception = await Assert.ThrowsAsync<AgentRunFailedException>(() =>
            CollectAsync(adapter.RunStreamingAsync("go")));

        Assert.Same(error, exception.Error);
        Assert.Equal("workflow_cycle_detected", exception.Error.Code);
        Assert.False(exception.Error.IsRetryable);
        Assert.Equal("inner", exception.Error.Metadata["participant.id"]);
        Assert.Equal("outer > inner", exception.Error.Metadata["definition.stack"]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RunStreamingAsync_EmitsFinalTextExactlyOnce(
        bool includesDelta,
        bool includesMessageCompleted)
    {
        var events = new List<AgentRunEvent>();
        if (includesDelta)
        {
            events.Add(new AgentTextDelta("message-1", "Inner", "hello "));
        }

        if (includesMessageCompleted)
        {
            events.Add(new AgentMessageCompleted("message-1", new AgentOutputMessage("Inner", "hello world")));
        }

        events.Add(new AgentRunCompleted(new AgentRunResult
        {
            FinalMessage = new AgentOutputMessage("Inner", "hello world"),
            FinalMessageId = "message-1"
        }));

        var updates = await CollectAsync(CreateAdapter(
            CreateParentContext(),
            new StubParticipantInvoker(events)).RunStreamingAsync("go"));

        Assert.Equal("hello world", string.Concat(updates.Select(static update => update.Text)));
    }

    private static AgentRuntimeAIAgentAdapter CreateAdapter(
        AppAgentRunContext parentContext,
        IWorkflowParticipantInvoker invoker)
    {
        var participant = new WorkflowRuntimeParticipant
        {
            Id = "inner",
            DisplayName = "Inner",
            Summary = "Nested participant",
            RuntimeKind = AgentRuntimeKind.WorkflowAgent,
            Source = new ReferencedParticipantSource(new AgentDefinitionReference(
                AgentDefinitionKind.SavedWorkflow,
                "inner-workflow"))
        };

        var resolved = new ResolvedWorkflowParticipant
        {
            ParticipantId = "inner",
            DisplayName = "Inner",
            Summary = "Nested participant",
            RuntimeKind = AgentRuntimeKind.WorkflowAgent,
            Source = new ReferencedParticipantSource(new AgentDefinitionReference(
                AgentDefinitionKind.SavedWorkflow,
                "inner-workflow"))
        };

        return new AgentRuntimeAIAgentAdapter(
            participant,
            resolved,
            new AgentRuntimeCreationContext
            {
                Configuration = new AppChatConfiguration("model", [])
            },
            parentContext,
            invoker);
    }

    private static AppAgentRunContext CreateParentContext() =>
        new()
        {
            RunId = "outer-run",
            DefinitionStack =
            [
                new AgentRunFrame
                {
                    Definition = new AgentDefinitionReference(
                        AgentDefinitionKind.SavedWorkflow,
                        "outer-workflow"),
                    DisplayName = "Outer"
                }
            ]
        };

    private static async Task<List<AgentResponseUpdate>> CollectAsync(
        IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        var result = new List<AgentResponseUpdate>();
        await foreach (var update in updates)
        {
            result.Add(update);
        }

        return result;
    }

    private sealed class StubParticipantInvoker(
        IReadOnlyList<AgentRunEvent> events) : IWorkflowParticipantInvoker
    {
        public AppAgentRunContext? ParentContext { get; private set; }

        public async IAsyncEnumerable<AgentRunEvent> InvokeAsync(
            ResolvedWorkflowParticipant participant,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AppAgentRunContext parentContext,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ParentContext = parentContext;
            foreach (var runEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return runEvent;
            }
        }
    }

    private sealed class NonExecutableRuntime : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } =
            new("inner", "Inner", string.Empty, AgentRuntimeKind.WorkflowAgent);

        public IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AppAgentRunContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
