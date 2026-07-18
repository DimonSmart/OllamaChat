using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.Agentic;
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
        var protocolExecutor = new AgentRuntimeProtocolExecutor(NullLogger<AgentRuntimeProtocolExecutor>.Instance);
        var runner = new AgentRunner(new AgentDefinitionExecutionDispatcher(
            new StubDefinitionCatalog(),
            new AgentRunNestingValidator(new AgentRuntimeOptions()),
            new AgentRuntimeFactory(
                new StubLlmFactory(),
                new StubWorkflowFactory()),
            protocolExecutor,
            NullLogger<AgentDefinitionExecutionDispatcher>.Instance));

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
                               RunId = Guid.NewGuid().ToString("N"),
                               DefinitionStack =
                               [
                                   new AgentRunFrame
                                   {
                                       Definition = reference,
                                       DisplayName = reference.Id
                                   }
                               ]
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
        [Obsolete]
        public Task<IAgentRuntime> CreateAsync(
            string workflowId,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default)
        {
            var workflow = new AgentWorkflowDefinition
            {
                Id = workflowId,
                DisplayName = "Workflow",
                StartAgentId = "agent",
                Agents =
                [
                    new AgentWorkflowAgentDefinition
                    {
                        Id = "agent",
                        Role = "agent",
                        Source = new InlineAgentParticipantSource(new AgentTemplateDefinition
                        {
                            Id = Guid.NewGuid(),
                            AgentName = "Agent",
                            Content = "Prompt"
                        })
                    }
                ]
            };

            return Task.FromResult<IAgentRuntime>(new WorkflowAgentRuntime(
                new AgentRuntimeDescriptor(workflowId, "Workflow", string.Empty, AgentRuntimeKind.WorkflowAgent),
                workflow,
                [],
                [],
                context.Configuration,
                context,
                new StubHeadlessWorkflowRunner(),
                new ThrowingWorkflowParticipantInvoker(),
                NullLogger<WorkflowAgentRuntime>.Instance));
        }
    }

    private sealed class ThrowingWorkflowParticipantInvoker : IWorkflowParticipantInvoker
    {
        public IAsyncEnumerable<AgentRunEvent> InvokeAsync(
            ResolvedWorkflowParticipant participant,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AgentRunContext parentContext,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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

    private sealed class StubHeadlessWorkflowRunner : IHeadlessWorkflowRunner
    {
        public Task<IHeadlessWorkflowSession> StartAsync(
            HeadlessWorkflowSessionStartRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IHeadlessWorkflowSession>(new StubHeadlessWorkflowSession());
    }

    private sealed class StubHeadlessWorkflowSession : IHeadlessWorkflowSession
    {
        public string TaskSessionId => "session-1";

        public async IAsyncEnumerable<HeadlessWorkflowEvent> RunTurnAsync(
            HeadlessWorkflowTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            const string messageId = "m1";
            yield return new HeadlessWorkflowStarted(TaskSessionId);
            yield return new HeadlessWorkflowTextDelta(messageId, "Workflow", "workflow answer");
            yield return new HeadlessWorkflowMessageCompleted(messageId, "agent", "Workflow", "workflow answer");
            yield return new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = messageId,
                FinalAuthor = "Workflow",
                FinalContent = "workflow answer",
                Messages = [new HeadlessWorkflowOutputMessage(messageId, "agent", "Workflow", "workflow answer")]
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
                Name = reference.Id,
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
