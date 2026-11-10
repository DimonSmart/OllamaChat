using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

/// <summary>
/// Characterization tests for the native Harness message injector.
/// These tests do not enable Steering in OllamaChat; they establish the MAF 1.27.0 lifecycle contract.
/// </summary>
public sealed class HarnessSteeringInjectionGateTests
{
    [Fact]
    public async Task LateMessage_DuringActiveModelCall_TriggersAdditionalModelCallInSameRun()
    {
        using var model = new RecordingClient(blockFirstCall: true);
        var agent = CreateHarness(model);
        AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        MessageInjectingChatClient injector = Assert.IsType<MessageInjectingChatClient>(
            agent.GetService<MessageInjectingChatClient>());

        Task run = DrainAsync(agent, session, "initial", TestContext.Current.CancellationToken);
        await model.FirstCallStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await injector.EnqueueMessagesAsync(
            session,
            [new ChatMessage(ChatRole.User, "use SQLite")],
            TestContext.Current.CancellationToken);

        model.ReleaseFirstCall.TrySetResult();
        await run;

        Assert.Equal(2, model.Requests.Count);
        Assert.DoesNotContain(model.Requests[0], m => m.Role == ChatRole.User && m.Text == "use SQLite");
        Assert.Single(model.Requests[1], m => m.Role == ChatRole.User && m.Text == "use SQLite");
        Assert.Empty(await injector.GetPendingMessagesAsync(session, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UndeliveredNativeMessage_RemainsInSessionAndLeaksIntoFollowingRun()
    {
        using var model = new RecordingClient(blockFirstCall: false);
        var agent = CreateHarness(model);
        AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        MessageInjectingChatClient injector = Assert.IsType<MessageInjectingChatClient>(
            agent.GetService<MessageInjectingChatClient>());

        await DrainAsync(agent, session, "first", TestContext.Current.CancellationToken);
        Assert.Single(model.Requests);

        // Models the window after the injector's final empty-queue check but
        // before the OllamaChat run service has closed admission.
        await injector.EnqueueMessagesAsync(
            session,
            [new ChatMessage(ChatRole.User, "stale steering")],
            TestContext.Current.CancellationToken);
        Assert.Single(await injector.GetPendingMessagesAsync(session, TestContext.Current.CancellationToken));

        await DrainAsync(agent, session, "second", TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Requests.Count);
        Assert.Single(model.Requests[1], m => m.Role == ChatRole.User && m.Text == "stale steering");
        Assert.Empty(await injector.GetPendingMessagesAsync(session, TestContext.Current.CancellationToken));
    }

    private static HarnessAgent CreateHarness(IChatClient client) => new(client, new HarnessAgentOptions
    {
        DisableTodoProvider = true,
        DisableAgentModeProvider = true,
        DisableFileMemory = true,
        DisableAgentSkillsProvider = true,
        DisableWebSearch = true,
        DisableToolAutoApproval = true,
        DisableOpenTelemetry = true
    });

    private static async Task DrainAsync(
        HarnessAgent agent, AgentSession session, string text, CancellationToken cancellationToken)
    {
        await foreach (var _ in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, text)],
                           session,
                           cancellationToken: cancellationToken))
        {
        }
    }

    private sealed class RecordingClient(bool blockFirstCall) : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);
            if (Requests.Count == 1)
            {
                FirstCallStarted.TrySetResult();
                if (blockFirstCall)
                    await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
