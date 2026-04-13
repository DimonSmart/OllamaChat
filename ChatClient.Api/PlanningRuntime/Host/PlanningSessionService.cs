using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class PlanningSessionService(
    IAppToolCatalog appToolCatalog,
    IPlanningRunExecutor planningRunExecutor,
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
        // Saved agents are intentionally not exposed to planning until the UI can opt in explicitly.
        var callableAgents = PlanningCallableAgentCatalog.Empty;

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();

        if (enabledTools.Count == 0)
            throw new InvalidOperationException("At least one planning tool must be enabled.");

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
            var observer = new ActionPlanRunObserver(HandleEvent);
            var loggerSink = new ActionExecutionLogger(HandleLogLine);
            var result = await planningRunExecutor.ExecuteAsync(
                new PlanningRunExecutionRequest
                {
                    UserQuery = userQuery,
                    Model = model,
                    EnabledTools = enabledTools,
                    CallableAgents = callableAgents,
                    ExecutionLogger = loggerSink,
                    PlanRunObserver = observer
                },
                cancellationToken);
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
                PreserveReusedStepState(reused.StepId);
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
        switch (trace.Outcome)
        {
            case StepTraceOutcome.Partial:
                step.Status = PlanStepStatuses.Partial;
                step.Error = new PlanStepError
                {
                    Code = trace.ErrorCode ?? "partial_failure",
                    Message = trace.ErrorMessage ?? "Step completed partially.",
                    Details = trace.ErrorDetails?.Clone()
                };
                return;

            case StepTraceOutcome.Done:
                step.Status = PlanStepStatuses.Done;
                step.Error = null;
                return;

            case StepTraceOutcome.Skipped:
                step.Status = PlanStepStatuses.Skip;
                step.Error = null;
                return;

            case StepTraceOutcome.Failed:
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

                return;

            default:
                throw new InvalidOperationException($"Unsupported trace outcome '{trace.Outcome}'.");
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

    private void PreserveReusedStepState(string stepId)
    {
        if (State.CurrentPlan is null)
            return;

        var step = State.CurrentPlan.Steps.FirstOrDefault(candidate => string.Equals(candidate.Id, stepId, StringComparison.Ordinal));
        if (step is null)
            return;

        if (PlanExecutionState.HasCompletedResult(step) || string.Equals(step.Status, PlanStepStatuses.Skip, StringComparison.Ordinal))
            return;

        step.Status = PlanStepStatuses.Done;
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
}
