using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class AgentRuntimePolymorphicIntegrationTests
{
    [Theory]
    [InlineData(AgentDefinitionKind.SavedAgent, "agent")]
    [InlineData(AgentDefinitionKind.SavedWorkflow, "workflow")]
    public async Task AgentRunner_RunsSavedAgentAndSavedWorkflowThroughSameContract(
        AgentDefinitionKind kind,
        string id)
    {
        var runner = new AgentRunner(
            new AgentRuntimeFactory(
                new StubLlmFactory(),
                new StubWorkflowFactory()),
            NullLogger<AgentRunner>.Instance);

        var events = await RunAndCollectAsync(runner, new AgentDefinitionReference(kind, id));

        Assert.Contains(events, static runEvent => runEvent is AgentTextDelta or AgentMessageCompleted);
        var terminal = Assert.Single(events, static runEvent => runEvent is AgentRunCompleted or AgentRunFailed);
        var completion = Assert.IsType<AgentRunCompleted>(terminal);
        Assert.False(string.IsNullOrWhiteSpace(completion.Result.FinalMessage.Content));
        Assert.IsType<AgentRunCompleted>(events.Last());
    }

    private static async Task<List<AgentRunEvent>> RunAndCollectAsync(
        AgentRunner runner,
        AgentDefinitionReference reference)
    {
        var result = new List<AgentRunEvent>();
        await foreach (var runEvent in runner.RunAsync(
                           reference,
                           new AgentRuntimeRunRequest
                           {
                               Messages = [new AgentInputMessage(AgentMessageRole.User, "go")]
                           },
                           new AgentRuntimeCreationContext
                           {
                               Configuration = new AppChatConfiguration("model", [])
                           },
                           new AgentRunContext
                           {
                               RunId = Guid.NewGuid().ToString("N")
                           }))
        {
            result.Add(runEvent);
        }

        return result;
    }

    private sealed class StubLlmFactory : ILlmAgentRuntimeFactory
    {
        public Task<IAgentRuntime> CreateAsync(
            string agentId,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAgentRuntime>(new StubRuntime(
                new AgentRuntimeDescriptor(agentId, "Leaf", string.Empty, AgentRuntimeKind.LlmAgent),
                "leaf answer"));
    }

    private sealed class StubWorkflowFactory : IWorkflowAgentRuntimeFactory
    {
        public Task<IAgentRuntime> CreateAsync(
            string workflowId,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAgentRuntime>(new StubRuntime(
                new AgentRuntimeDescriptor(workflowId, "Workflow", string.Empty, AgentRuntimeKind.WorkflowAgent),
                "workflow answer"));
    }

    private sealed class StubRuntime(
        AgentRuntimeDescriptor descriptor,
        string finalContent) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } = descriptor;

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            const string messageId = "m1";
            yield return new AgentTextDelta(messageId, Descriptor.Name, finalContent);
            var message = new AgentOutputMessage(Descriptor.Name, finalContent);
            yield return new AgentMessageCompleted(messageId, message);
            yield return new AgentRunCompleted(new AgentRunResult
            {
                FinalMessageId = messageId,
                FinalMessage = message,
                Messages = [message]
            });
        }
    }
}
