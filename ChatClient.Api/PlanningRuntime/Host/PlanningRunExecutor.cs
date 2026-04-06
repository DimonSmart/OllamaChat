using System.Text.Json;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Orchestration;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.PlanningRuntime.Host;

public interface IPlanningRunExecutor
{
    Task<ResultEnvelope<JsonElement?>> ExecuteAsync(
        PlanningRunExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PlanningRunExecutor(
    ILlmChatClientFactory chatClientFactory,
    IMcpUserInteractionService mcpUserInteractionService,
    IAgenticExecutionInvoker agenticExecutionInvoker) : IPlanningRunExecutor
{
    private const int MaxAttempts = 3;

    public async Task<ResultEnvelope<JsonElement?>> ExecuteAsync(
        PlanningRunExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatClient = await chatClientFactory.CreateAsync(request.Model, cancellationToken);
        var observer = request.PlanRunObserver ?? NullPlanRunObserver.Instance;
        var loggerSink = request.ExecutionLogger ?? NullExecutionLogger.Instance;
        var callableAgents = request.CallableAgents ?? PlanningCallableAgentCatalog.Empty;
        var toolCatalog = new PlanningToolCatalog(request.EnabledTools);
        var initialDraftRepairer = new LlmInitialDraftRepairer(
            chatClient,
            toolCatalog,
            loggerSink,
            observer,
            callableAgents);
        var planner = new LlmPlanner(
            chatClient,
            toolCatalog,
            loggerSink,
            observer,
            initialDraftRepairer,
            callableAgents);
        var replanner = new LlmReplanner(
            chatClient,
            toolCatalog,
            loggerSink,
            observer,
            callableAgents);
        var runner = new AgentStepRunner(
            chatClient,
            agenticExecutionInvoker,
            callableAgents,
            observer);
        var executor = new PlanExecutor(
            toolCatalog,
            runner,
            loggerSink,
            observer,
            mcpUserInteractionService);
        var finalAnswerVerifier = new LlmFinalAnswerVerifier(chatClient);
        var orchestrator = new PlanningOrchestrator(
            planner,
            executor,
            new GoalVerifier(askUserEnabled: true),
            loggerSink,
            maxAttempts: MaxAttempts,
            replanner: replanner,
            finalAnswerVerifier: finalAnswerVerifier,
            planRunObserver: observer);

        return await orchestrator.RunAsync(request.UserQuery, cancellationToken);
    }
}

public sealed class PlanningRunExecutionRequest
{
    public required string UserQuery { get; init; }

    public required ServerModel Model { get; init; }

    public required IReadOnlyCollection<AppToolDescriptor> EnabledTools { get; init; }

    public PlanningCallableAgentCatalog? CallableAgents { get; init; }

    public IExecutionLogger? ExecutionLogger { get; init; }

    public IPlanRunObserver? PlanRunObserver { get; init; }
}
