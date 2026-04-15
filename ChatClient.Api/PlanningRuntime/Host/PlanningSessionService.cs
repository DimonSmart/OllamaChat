using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Shared;
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
            () => ExecuteRunAsync(request.UserQuery, planner.Model, enabledTools, _runCts.Token),
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
        State.ActiveRuntimeStepId = null;
        State.RequestBrief = null;
        State.OutlinePlan = null;
        State.OutlineRawResponse = null;
        State.LowLevelPlan = null;
        State.LowLevelRawResponse = null;
        State.RuntimePlan = null;
        State.CurrentPlan = null;
        State.FinalResult = null;
        lock (State.Issues)
        {
            State.Issues.Clear();
        }
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
            State.FinalResult = ResultEnvelope<JsonElement?>.Failure("planning_failed", ex.Message);
            HandleLogLine($"[planning] error={ex.Message}");
        }
        finally
        {
            State.IsRunning = false;
            State.IsCompleted = true;
            State.ActiveStepId = null;
            State.ActiveRuntimeStepId = null;
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
            case RequestAnalysisCompletedEvent analysisCompleted:
                State.RequestBrief = analysisCompleted.Brief;
                break;

            case OutlineStageCompletedEvent outlineCompleted:
                State.OutlinePlan = outlineCompleted.Plan;
                State.OutlineRawResponse = string.IsNullOrWhiteSpace(outlineCompleted.RawResponse)
                    ? null
                    : outlineCompleted.RawResponse;
                ReplaceIssues("outline", outlineCompleted.Issues);
                break;

            case LowLevelStageCompletedEvent lowLevelCompleted:
                State.LowLevelPlan = lowLevelCompleted.Plan;
                State.LowLevelRawResponse = string.IsNullOrWhiteSpace(lowLevelCompleted.RawResponse)
                    ? null
                    : lowLevelCompleted.RawResponse;
                ReplaceIssues("low_level", lowLevelCompleted.Issues);
                break;

            case RuntimeCompilationCompletedEvent runtimeCompiled:
                State.RuntimePlan = runtimeCompiled.Plan;
                ReplaceIssues("runtime", runtimeCompiled.Issues);
                break;

            case RuntimeStepStartedEvent runtimeStepStarted:
                State.ActiveRuntimeStepId = runtimeStepStarted.StepId;
                State.ActiveStepId = runtimeStepStarted.StepId;
                break;

            case RuntimeStepCompletedEvent runtimeStepCompleted:
                if (string.Equals(State.ActiveRuntimeStepId, runtimeStepCompleted.StepId, StringComparison.OrdinalIgnoreCase))
                {
                    State.ActiveRuntimeStepId = null;
                    State.ActiveStepId = null;
                }
                break;

            case RunCompletedEvent completed:
                State.FinalResult = CloneEnvelope(completed.Result);
                ReplaceIssues(
                    "runtime_execution",
                    IsRuntimeExecutionFailure(completed.Result)
                        ? ExtractIssues(completed.Result.Error?.Details)
                        : []);
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

    private void ReplaceIssues(string layerPrefix, IReadOnlyList<PlanningIssue> issues)
    {
        lock (State.Issues)
        {
            State.Issues.RemoveAll(issue => issue.Layer.StartsWith(layerPrefix, StringComparison.OrdinalIgnoreCase));
            State.Issues.AddRange(issues);
        }
    }

    private static bool IsRuntimeExecutionFailure(ResultEnvelope<JsonElement?> result) =>
        !result.Ok
        && string.Equals(result.Error?.Code, "runtime_execution_failed", StringComparison.OrdinalIgnoreCase);

    private static List<PlanningIssue> ExtractIssues(JsonElement? details)
    {
        var issues = new List<PlanningIssue>();
        if (details is not { } value
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || !value.TryGetProperty("issues", out var issuesElement)
            || issuesElement.ValueKind != JsonValueKind.Array)
        {
            return issues;
        }

        foreach (var issueElement in issuesElement.EnumerateArray())
        {
            var layer = issueElement.TryGetProperty("layer", out var layerElement)
                ? layerElement.GetString()
                : null;
            var code = issueElement.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : null;
            var message = issueElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(layer)
                || string.IsNullOrWhiteSpace(code)
                || string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            issues.Add(new PlanningIssue
            {
                Layer = layer,
                Code = code,
                Message = message
            });
        }

        return issues;
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }

    private static ResultEnvelope<JsonElement?> CloneEnvelope(ResultEnvelope<JsonElement?> result) =>
        result.Ok
            ? ResultEnvelope<JsonElement?>.Success(result.Data?.Clone())
            : ResultEnvelope<JsonElement?>.Failure(
                result.Error?.Code ?? "planning_failed",
                result.Error?.Message ?? "Planning failed.",
                result.Error?.Details?.Clone());
}
