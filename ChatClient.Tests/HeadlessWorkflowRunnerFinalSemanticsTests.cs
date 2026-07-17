using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Services.TaskSessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class HeadlessWorkflowRunnerFinalSemanticsTests
{
    [Fact]
    public async Task Sequential_UsesLastMessageFromLastAgentInDeclaredOrder()
    {
        var workflow = new SequentialWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            AgentOrder = ["a", "b"]
        };

        var messages = new[]
        {
            Message("a1", "a", "Agent A", "first"),
            Message("b1", "b", "Agent B", "second"),
            Message("a2", "a", "Agent A", "late from a")
        };

        var result = await ResolveAsync(workflow, messages);

        Assert.Equal("second", result!.FinalContent);
        Assert.Equal(messages[1].Message.Id.ToString("N"), result.FinalMessageId);
    }

    [Fact]
    public async Task Sequential_FallsBackToLastMessage()
    {
        var workflow = new SequentialWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            AgentOrder = ["missing"]
        };

        var result = await ResolveAsync(workflow, [
            Message("a1", "a", "Agent A", "first"),
            Message("b1", "b", "Agent B", "second")
        ]);

        Assert.Equal("second", result!.FinalContent);
    }

    [Fact]
    public async Task Concurrent_LastMessagePerAgent_UsesDeclarationOrderNotCompletionOrder()
    {
        var workflow = new ConcurrentWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            ParticipantAgentIds = ["a", "b"]
        };

        var messages = new[]
        {
            Message("b1", "b", "Beta", "beta"),
            Message("a1", "a", "Alpha", "alpha")
        };

        var result = await ResolveAsync(workflow, messages);

        Assert.Contains("## Alpha", result!.FinalContent);
        Assert.Contains("## Beta", result.FinalContent);
        Assert.True(result.FinalContent.IndexOf("## Alpha", StringComparison.Ordinal) <
                    result.FinalContent.IndexOf("## Beta", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Message.Id.ToString("N") == result.FinalMessageId);
    }

    [Fact]
    public async Task Concurrent_ConcatenateAllMessages_PreservesParticipantOrderAndUnknownsLast()
    {
        var workflow = new ConcurrentWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            ParticipantAgentIds = ["a", "b"],
            Aggregation = new ConcurrentWorkflowAggregationDefinition
            {
                Kind = ConcurrentWorkflowAggregationKind.ConcatenateAllMessages
            }
        };

        var result = await ResolveAsync(workflow, [
            Message("b1", "b", "Beta", "b1"),
            Message("x1", "x", "Unknown", "x1"),
            Message("a1", "a", "Alpha", "a1"),
            Message("a2", "a", "Alpha", "a2")
        ]);

        Assert.Equal(
            string.Join(Environment.NewLine + Environment.NewLine, ["a1", "a2", "b1", "x1"]),
            result!.FinalContent);
    }

    [Fact]
    public async Task GroupChat_UsesCompletionSummaryWhenAvailable()
    {
        var store = CreateTaskSessionStore();
        var session = await store.CreateSessionAsync("group", null, CancellationToken.None);
        await store.SaveSummaryAsync(session.SessionId, "final", "summary", CancellationToken.None);
        var workflow = new GroupChatWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            Execution = new AgentWorkflowExecutionDefinition
            {
                CompletionSummaryLabel = "final"
            }
        };

        var messages = new[] { Message("a1", "a", "Agent", "message") };

        var result = await ResolveAsync(workflow, messages, store, session.SessionId);

        Assert.Equal("summary", result!.FinalContent);
        Assert.DoesNotContain(messages, message => message.Message.Id.ToString("N") == result.FinalMessageId);
    }

    [Fact]
    public async Task GroupChat_FallsBackToLastParticipantMessageAndReturnsNullWhenNoResult()
    {
        var workflow = new GroupChatWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow"
        };

        var result = await ResolveAsync(workflow, [
            Message("a1", "a", "Agent", "first"),
            Message("b1", "b", "Agent", "second")
        ]);
        var empty = await ResolveAsync(workflow, []);

        Assert.Equal("second", result!.FinalContent);
        Assert.Null(empty);
    }

    [Fact]
    public async Task HandoffAndGenericWorkflow_UseLastContentMessage()
    {
        var workflow = new AgentWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            StartAgentId = "a"
        };

        var messages = new[]
        {
            Message("a1", "a", "Agent", "first"),
            Message("b1", "b", "Agent", "second")
        };

        var result = await ResolveAsync(workflow, messages);

        Assert.Equal("second", result!.FinalContent);
        Assert.Equal(messages[1].Message.Id.ToString("N"), result.FinalMessageId);
    }

    private static Task<HeadlessWorkflowResult?> ResolveAsync(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages,
        TaskSessionStore? taskSessionStore = null,
        string taskSessionId = "task") =>
        new HeadlessWorkflowRunner(
            null!,
            null!,
            null!,
            taskSessionStore ?? CreateTaskSessionStore(),
            NullLogger<HeadlessWorkflowRunner>.Instance).ResolveFinalMessageAsync(
            new HeadlessWorkflowRunRequest
            {
                Workflow = workflow,
                Agents = [],
                Configuration = new AppChatConfiguration("model", []),
                SessionTitle = "Workflow"
            },
            taskSessionId,
            messages,
            CancellationToken.None);

    private static OrchestrationCompletedAssistantMessage Message(
        string stableKey,
        string participantId,
        string author,
        string content)
    {
        var bytes = new byte[16];
        var keyBytes = System.Text.Encoding.ASCII.GetBytes(stableKey);
        Array.Copy(keyBytes, bytes, Math.Min(keyBytes.Length, bytes.Length));
        var id = new Guid(bytes);
        return new OrchestrationCompletedAssistantMessage(
            new AppChatMessage(content, DateTime.Now, AppChatRole.Assistant, agentId: participantId, agentName: author)
            {
                Id = id
            },
            participantId);
    }

    private static TaskSessionStore CreateTaskSessionStore()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "OllamaChat.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var binding = new McpServerSessionBinding();
        binding.Parameters[TaskSessionStore.DatabaseFileParameter] = Path.Combine(tempDirectory, "task-sessions.db");
        return new TaskSessionStore(
            new McpServerSessionContext(binding),
            new SqliteTaskSessionRepository());
    }
}
