using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.AgentWorkflows.Runtime;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

public sealed class HandoffRuntimeWorkflowBuilderTests
{
    [Fact]
    public void SelectDistinctHandoffsForRuntime_DeduplicatesSameAgentPair()
    {
        var handoffs = new[]
        {
            new AgentWorkflowHandoffDefinition
            {
                FromAgentId = "kant",
                ToAgentId = "host",
                Label = "fallback",
                IsFallback = true
            },
            new AgentWorkflowHandoffDefinition
            {
                FromAgentId = "kant",
                ToAgentId = "host",
                Label = "host intervention or conclusion",
                IsFallback = false
            },
            new AgentWorkflowHandoffDefinition
            {
                FromAgentId = "host",
                ToAgentId = "kant",
                Label = "open with Kant",
                IsFallback = false
            }
        };

        var selected = HandoffRuntimeWorkflowBuilder.SelectDistinctHandoffsForRuntime(handoffs);

        Assert.Equal(2, selected.Count);
        var kantToHost = Assert.Single(selected, static handoff =>
            string.Equals(handoff.FromAgentId, "kant", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(handoff.ToAgentId, "host", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("host intervention or conclusion", kantToHost.Label);
        Assert.False(kantToHost.IsFallback);
    }

    [Fact]
    public void SelectHandoffsForRuntime_SkipsTargetAgentStillOnCooldown()
    {
        var workflow = CreateWorkflowDefinition(
            new AgentWorkflowAgentDefinition
            {
                Id = "host",
                Role = "Host"
            },
            new AgentWorkflowAgentDefinition
            {
                Id = "kant",
                Role = "Kant",
                MinAssistantTurnsBetweenTurns = 2
            },
            new AgentWorkflowAgentDefinition
            {
                Id = "nietzsche",
                Role = "Nietzsche",
                MinAssistantTurnsBetweenTurns = 2
            },
            new AgentWorkflowHandoffDefinition
            {
                FromAgentId = "host",
                ToAgentId = "kant",
                Label = "open with Kant"
            },
            new AgentWorkflowHandoffDefinition
            {
                FromAgentId = "host",
                ToAgentId = "nietzsche",
                Label = "open with Nietzsche"
            });

        var selected = HandoffRuntimeWorkflowBuilder.SelectHandoffsForRuntime(
            workflow,
            ["host", "kant", "host"]);

        Assert.DoesNotContain(selected, static handoff =>
            string.Equals(handoff.FromAgentId, "host", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(handoff.ToAgentId, "kant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(selected, static handoff =>
            string.Equals(handoff.FromAgentId, "host", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(handoff.ToAgentId, "nietzsche", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectHandoffsForRuntime_SkipsTargetAgentAtTurnLimit()
    {
        var workflow = CreateWorkflowDefinition(
            new AgentWorkflowAgentDefinition
            {
                Id = "host",
                Role = "Host"
            },
            new AgentWorkflowAgentDefinition
            {
                Id = "kant",
                Role = "Kant",
                MaxTurnsPerSession = 3
            },
            new AgentWorkflowAgentDefinition
            {
                Id = "nietzsche",
                Role = "Nietzsche"
            },
            new AgentWorkflowHandoffDefinition
            {
                FromAgentId = "host",
                ToAgentId = "kant",
                Label = "open with Kant"
            },
            new AgentWorkflowHandoffDefinition
            {
                FromAgentId = "host",
                ToAgentId = "nietzsche",
                Label = "open with Nietzsche"
            });

        var selected = HandoffRuntimeWorkflowBuilder.SelectHandoffsForRuntime(
            workflow,
            ["kant", "host", "kant", "host", "kant", "host"]);

        Assert.DoesNotContain(selected, static handoff =>
            string.Equals(handoff.FromAgentId, "host", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(handoff.ToAgentId, "kant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(selected, static handoff =>
            string.Equals(handoff.FromAgentId, "host", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(handoff.ToAgentId, "nietzsche", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenStreamingAsync_AllowsSequentialConversationAndTurnTokenBatches()
    {
        var executor = new ManualTurnChatExecutor();
        var workflow = new WorkflowBuilder(executor)
            .WithOutputFrom(executor)
            .Build();

        await using var run = await InProcessExecution.OpenStreamingAsync(workflow, "handoff-regression");

        var conversationAccepted = await run.TrySendMessageAsync<IEnumerable<ChatMessage>>(
            [new ChatMessage(ChatRole.User, "hello handoff")]);
        Assert.True(conversationAccepted);

        var initialBatchEvents = await CollectEventsAsync(run);
        Assert.DoesNotContain(initialBatchEvents, static workflowEvent => workflowEvent is WorkflowOutputEvent);

        var turnAccepted = await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        Assert.True(turnAccepted);

        var outputBatchEvents = await CollectEventsAsync(run);
        var outputEvent = Assert.Single(outputBatchEvents.OfType<WorkflowOutputEvent>());
        var message = Assert.Single(ExtractOutputMessages(outputEvent));
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal("hello handoff", message.Text);
    }

    private static async Task<List<WorkflowEvent>> CollectEventsAsync(StreamingRun run)
    {
        List<WorkflowEvent> workflowEvents = [];

        await foreach (var workflowEvent in run.WatchStreamAsync())
        {
            workflowEvents.Add(workflowEvent);
        }

        return workflowEvents;
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

        return [];
    }

    private static AgentWorkflowDefinition CreateWorkflowDefinition(
        AgentWorkflowAgentDefinition host,
        AgentWorkflowAgentDefinition first,
        AgentWorkflowAgentDefinition second,
        params AgentWorkflowHandoffDefinition[] handoffs)
    {
        return new AgentWorkflowDefinition
        {
            Id = "test-workflow",
            DisplayName = "Test Workflow",
            StartAgentId = host.Id,
            Agents = [host, first, second],
            Handoffs = handoffs.ToList()
        };
    }

    private sealed class ManualTurnChatExecutor()
        : ChatProtocolExecutor(
            "manual-turn-chat",
            new ChatProtocolExecutorOptions
            {
                StringMessageChatRole = ChatRole.User,
                AutoSendTurnToken = false
            })
    {
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
            base.ConfigureProtocol(protocolBuilder)
                .SendsMessage<ChatMessage>()
                .YieldsOutput<ChatMessage>();

        protected override ValueTask TakeTurnAsync(
            List<ChatMessage> messages,
            IWorkflowContext context,
            bool? emitEvents,
            CancellationToken cancellationToken = default)
        {
            var latestUserMessage = messages.LastOrDefault(static message => message.Role == ChatRole.User)?.Text
                ?? string.Empty;
            return context.YieldOutputAsync(
                new ChatMessage(ChatRole.Assistant, latestUserMessage),
                cancellationToken);
        }
    }
}
