using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Orchestration;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class PlanningSessionService(
    IPlanningChatClientFactory chatClientFactory,
    WebSearchTool searchTool,
    WebDownloadTool downloadTool,
    ILogger<PlanningSessionService> logger) : IPlanningSessionService
{
    private readonly Dictionary<string, ITool> _availableTools = new(StringComparer.OrdinalIgnoreCase)
    {
        [searchTool.Name] = searchTool,
        [downloadTool.Name] = downloadTool
    };

    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public PlanningSessionState State { get; } = new();

    public event Action? StateChanged;

    public Task StartAsync(PlanningRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserQuery))
            throw new InvalidOperationException("User query is required.");
        if (request.EnabledToolNames.Count == 0)
            throw new InvalidOperationException("At least one planning tool must be enabled.");

        var enabledToolOptions = request.EnabledToolNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => _availableTools.TryGetValue(name, out var tool)
                ? new PlanningToolOption
                {
                    Name = tool.Name,
                    DisplayName = tool.PlannerMetadata.Name,
                    Description = tool.PlannerMetadata.Description
                }
                : throw new InvalidOperationException($"Planning tool '{name}' is not available."))
            .ToList();

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();

        Reset();
        State.UserQuery = request.UserQuery.Trim();
        State.IsRunning = true;
        lock (State.AvailableTools)
        {
            State.AvailableTools.AddRange(enabledToolOptions);
        }
        NotifyStateChanged();

        _runTask = Task.Run(() => ExecuteRunAsync(request, _runCts.Token), _runCts.Token);
        return Task.CompletedTask;
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

    private async Task ExecuteRunAsync(PlanningRunRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var chatClient = await chatClientFactory.CreateAsync(request.Model, cancellationToken);
            var enabledTools = request.EnabledToolNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => _availableTools.TryGetValue(name, out var tool)
                    ? tool
                    : throw new InvalidOperationException($"Planning tool '{name}' is not available."))
                .ToList();
            var observer = new ActionPlanRunObserver(HandleEvent);
            var loggerSink = new ActionExecutionLogger(HandleLogLine);
            var registry = new ToolRegistry(enabledTools);
            var planner = new LlmPlanner(chatClient, registry, loggerSink, observer);
            var replanner = new LlmReplanner(chatClient, registry, loggerSink, observer);
            var runner = new AgentStepRunner(chatClient, observer);
            var executor = new PlanExecutor(registry, runner, loggerSink, observer);
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

            var result = await orchestrator.RunAsync(request.UserQuery, cancellationToken);
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
}
