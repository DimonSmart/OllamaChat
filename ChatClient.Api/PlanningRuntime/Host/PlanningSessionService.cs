using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Orchestration;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class PlanningSessionService(
    ILlmChatClientFactory chatClientFactory,
    IAppToolCatalog appToolCatalog,
    IMcpUserInteractionService mcpUserInteractionService,
    IAgentDescriptionService agentDescriptionService,
    IModelCapabilityService modelCapabilityService,
    IAgenticExecutionInvoker agenticExecutionInvoker,
    ILogger<PlanningSessionService> logger) : IPlanningSessionService
{
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public PlanningSessionState State { get; } = new();

    public event Action? StateChanged;

    public async Task StartAsync(PlanningRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserQuery))
            throw new InvalidOperationException("User query is required.");
        var planner = request.Planner;

        var plannerBindings = planner.Agent.McpServerBindings;
        var hasConfiguredBindings = plannerBindings.Any(static binding => binding.Enabled && binding.HasIdentity);
        var enabledTools = hasConfiguredBindings
            ? McpBindingToolSelectionResolver.FilterAvailableTools(
                plannerBindings,
                await appToolCatalog.ListToolsAsync(new McpClientRequestContext(plannerBindings)))
                .ToList()
            : [];
        var enabledToolOptions = enabledTools
            .Select(tool => new PlanningToolOption
            {
                Name = tool.QualifiedName,
                DisplayName = string.IsNullOrWhiteSpace(tool.DisplayName)
                    ? tool.QualifiedName
                    : $"{tool.ServerName}: {tool.DisplayName}",
                Description = tool.Description
            })
            .ToList();
        var callableAgents = await BuildCallableAgentCatalogAsync(planner, CancellationToken.None);

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();

        if (enabledTools.Count == 0 && callableAgents.ListAgents().Count == 0)
            throw new InvalidOperationException("At least one planning tool or callable saved agent must be enabled.");

        Reset();
        State.UserQuery = request.UserQuery.Trim();
        State.IsRunning = true;
        lock (State.AvailableTools)
        {
            State.AvailableTools.AddRange(enabledToolOptions);
        }
        NotifyStateChanged();

        _runTask = Task.Run(
            () => ExecuteRunAsync(request.UserQuery, planner.Model, enabledTools, callableAgents, _runCts.Token),
            _runCts.Token);
    }

    public async Task CancelAsync()
    {
        _runCts?.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Reset()
    {
        State.UserQuery = string.Empty;
        State.IsRunning = false;
        State.IsCompleted = false;
        State.ActiveStepId = null;
        State.CurrentPlan = null;
        State.FinalResult = null;
        lock (State.Events)
        {
            State.Events.Clear();
        }
        lock (State.LogLines)
        {
            State.LogLines.Clear();
        }
        lock (State.AvailableTools)
        {
            State.AvailableTools.Clear();
        }
        NotifyStateChanged();
    }

    private async Task ExecuteRunAsync(
        string userQuery,
        ServerModel model,
        IReadOnlyCollection<AppToolDescriptor> enabledTools,
        PlanningCallableAgentCatalog callableAgents,
        CancellationToken cancellationToken)
    {
        try
        {
            var chatClient = await chatClientFactory.CreateAsync(model, cancellationToken);
            var observer = new ActionPlanRunObserver(HandleEvent);
            var loggerSink = new ActionExecutionLogger(HandleLogLine);
            var registry = new PlanningToolCatalog(enabledTools);
            var initialDraftRepairer = new LlmInitialDraftRepairer(chatClient, registry, loggerSink, observer, callableAgents);
            var planner = new LlmPlanner(chatClient, registry, loggerSink, observer, initialDraftRepairer, callableAgents);
            var replanner = new LlmReplanner(chatClient, registry, loggerSink, observer, callableAgents);
            var runner = new AgentStepRunner(chatClient, agenticExecutionInvoker, callableAgents, observer);
            var executor = new PlanExecutor(registry, runner, loggerSink, observer, mcpUserInteractionService);
            var finalAnswerVerifier = new LlmFinalAnswerVerifier(chatClient);
            var orchestrator = new PlanningOrchestrator(
                planner,
                executor,
                new GoalVerifier(askUserEnabled: true),
                loggerSink,
                maxAttempts: 3,
                replanner: replanner,
                finalAnswerVerifier: finalAnswerVerifier,
                planRunObserver: observer);

            var result = await orchestrator.RunAsync(userQuery, cancellationToken);
            State.FinalResult = CloneEnvelope(result);
        }
        catch (OperationCanceledException)
        {
            State.FinalResult = null;
            HandleLogLine("[planning] canceled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Planning session failed.");
            State.FinalResult = ChatClient.Api.PlanningRuntime.Common.ResultEnvelope<JsonElement?>.Failure("planning_failed", ex.Message);
            HandleLogLine($"[planning] error={ex.Message}");
        }
        finally
        {
            State.IsRunning = false;
            State.IsCompleted = true;
            State.ActiveStepId = null;
            NotifyStateChanged();
        }
    }

    private void HandleEvent(PlanRunEvent planRunEvent)
    {
        lock (State.Events)
        {
            State.Events.Add(planRunEvent);
        }

        switch (planRunEvent)
        {
            case PlanCreatedEvent created:
                State.CurrentPlan = ClonePlan(created.Plan);
                break;

            case ReplanAppliedEvent replanned:
                State.CurrentPlan = ClonePlan(replanned.Plan);
                break;

            case StepStartedEvent started:
                State.ActiveStepId = started.StepId;
                MarkStepRunning(started.StepId);
                break;

            case StepReusedEvent reused:
                UpdateStepFromTrace(new StepExecutionTrace
                {
                    StepId = reused.StepId,
                    Success = true,
                    Reused = true
                }, null);
                break;

            case StepCompletedEvent completed:
                UpdateStepFromTrace(completed.Trace, completed.Result);
                break;

            case RunCompletedEvent completed:
                State.FinalResult = CloneEnvelope(completed.Result);
                break;
        }

        NotifyStateChanged();
    }

    private void HandleLogLine(string line)
    {
        lock (State.LogLines)
        {
            State.LogLines.Add(line);
        }
        NotifyStateChanged();
    }

    private void UpdateStepFromTrace(StepExecutionTrace trace, JsonElement? result)
    {
        if (State.CurrentPlan is null)
            return;

        var step = State.CurrentPlan.Steps.FirstOrDefault(candidate => string.Equals(candidate.Id, trace.StepId, StringComparison.Ordinal));
        if (step is null)
            return;

        step.Result = result?.Clone();
        if (trace.Success)
        {
            step.Status = PlanStepStatuses.Done;
            step.Error = null;
            return;
        }

        step.Status = PlanStepStatuses.Fail;
        if (!string.IsNullOrWhiteSpace(trace.ErrorCode) || !string.IsNullOrWhiteSpace(trace.ErrorMessage))
        {
            step.Error = new PlanStepError
            {
                Code = trace.ErrorCode ?? "execution_failed",
                Message = trace.ErrorMessage ?? "Execution failed.",
                Details = trace.ErrorDetails?.Clone()
            };
        }
    }

    private void MarkStepRunning(string stepId)
    {
        if (State.CurrentPlan is null)
            return;

        var step = State.CurrentPlan.Steps.FirstOrDefault(candidate => string.Equals(candidate.Id, stepId, StringComparison.Ordinal));
        if (step is null)
            return;

        step.Status = PlanStepStatuses.Running;
        step.Error = null;
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }

    private static PlanDefinition ClonePlan(PlanDefinition plan) =>
        JsonSerializer.Deserialize<PlanDefinition>(JsonSerializer.Serialize(plan))
        ?? throw new InvalidOperationException("Failed to clone planning state.");

    private static ChatClient.Api.PlanningRuntime.Common.ResultEnvelope<JsonElement?> CloneEnvelope(ChatClient.Api.PlanningRuntime.Common.ResultEnvelope<JsonElement?> result) =>
        result.Ok
            ? ChatClient.Api.PlanningRuntime.Common.ResultEnvelope<JsonElement?>.Success(result.Data?.Clone())
            : ChatClient.Api.PlanningRuntime.Common.ResultEnvelope<JsonElement?>.Failure(
                result.Error?.Code ?? "planning_failed",
                result.Error?.Message ?? "Planning failed.",
                result.Error?.Details?.Clone());

    private async Task<PlanningCallableAgentCatalog> BuildCallableAgentCatalogAsync(
        ResolvedChatAgent planner,
        CancellationToken cancellationToken)
    {
        var savedAgents = await agentDescriptionService.GetAllAsync();
        List<ResolvedChatAgent> resolvedAgents = [];

        foreach (var agent in savedAgents)
        {
            if (agent.Id == planner.Agent.Id)
                continue;

            if (agent.LlmId is not Guid serverId || string.IsNullOrWhiteSpace(agent.ModelName))
                continue;

            var model = new ServerModel(serverId, agent.ModelName.Trim());

            try
            {
                await modelCapabilityService.EnsureModelSupportedByServerAsync(model, cancellationToken);
                resolvedAgents.Add(AgentDescriptionFactory.CreateResolved(agent, model));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(
                    ex,
                    "Skipping callable saved agent {AgentName} because its model could not be resolved for planning.",
                    agent.AgentName);
            }
        }

        if (resolvedAgents.Count == 0)
            return PlanningCallableAgentCatalog.Empty;

        var preferredNames = resolvedAgents.ToDictionary(
            resolvedAgent => resolvedAgent.Agent.Id,
            static resolvedAgent => string.IsNullOrWhiteSpace(resolvedAgent.Agent.ShortName)
                ? resolvedAgent.Agent.Id.ToString("D")
                : resolvedAgent.Agent.ShortName.Trim());
        var duplicateNames = preferredNames.Values
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var descriptors = resolvedAgents
            .Select(resolvedAgent =>
            {
                var name = preferredNames[resolvedAgent.Agent.Id];
                if (duplicateNames.Contains(name))
                    name = resolvedAgent.Agent.Id.ToString("D");

                var description = PlanningLogFormatter.SummarizeText(resolvedAgent.Agent.Content, 220);
                if (string.IsNullOrWhiteSpace(description) || string.Equals(description, "<empty>", StringComparison.Ordinal))
                    description = $"Saved agent using model '{resolvedAgent.Model.ModelName}'.";

                return new PlanningCallableAgentDescriptor
                {
                    Name = name,
                    DisplayName = resolvedAgent.Agent.AgentName,
                    Description = description,
                    Agent = resolvedAgent
                };
            })
            .ToList();

        return new PlanningCallableAgentCatalog(descriptors);
    }
}
