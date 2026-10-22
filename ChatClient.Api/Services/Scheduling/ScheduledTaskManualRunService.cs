using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChatClient.Api.Services.Scheduling;

public enum ManualRunStatus { Running, Completed, Failed, Canceled, TimedOut }

public sealed record ManualRunState(
    Guid ManualRunId,
    Guid ScheduledTaskId,
    Guid AgentId,
    string TimerName,
    string AgentName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    ManualRunStatus Status,
    string? AgentRunId = null,
    string? FinalText = null,
    bool IsTruncated = false,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? CancellationRequested = null);

public sealed record ManualRunStartResult(bool Accepted, ManualRunState? Run, string? Message = null);

public interface IScheduledTaskManualRunService
{
    event Action? StateChanged;
    ManualRunState? Current { get; }
    ManualRunState? ForTimer(Guid taskId);
    ManualRunState? Find(Guid runId);
    Task<ManualRunStartResult> RunNowAsync(Guid taskId);
    bool Cancel(Guid manualRunId);
    void TimerDeleted(Guid taskId);
    void TimerCreated(Guid taskId);
}

public sealed class ScheduledTaskManualRunOptions
{
    public const string SectionName = "ScheduledTasks:ManualRun";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);
    public const int MaximumResultLength = 64 * 1024;
}

/// <summary>A host-owned, process-local single-flight execution coordinator.</summary>
public sealed class ScheduledTaskManualRunService(
    IServiceScopeFactory scopes,
    IScheduledTaskRepository repository,
    ScheduledTaskRuntimeState schedulerState,
    IOptions<ScheduledTaskManualRunOptions> options,
    TimeProvider clock,
    ILogger<ScheduledTaskManualRunService> logger)
    : IScheduledTaskManualRunService, IHostedService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ManualRunState> _last = [];
    private readonly HashSet<Guid> _deletedDuringRun = [];
    private readonly HashSet<Guid> _deletedTimers = [];
    private ManualRunState? _current;
    private CancellationTokenSource? _cancellation;
    private Task? _execution;
    private bool _stopping;

    public event Action? StateChanged;

    public ManualRunState? Current { get { lock (_gate) return _current; } }
    public ManualRunState? ForTimer(Guid taskId)
    {
        lock (_gate)
            return _current?.ScheduledTaskId == taskId ? _current : _last.GetValueOrDefault(taskId);
    }
    public ManualRunState? Find(Guid runId)
    {
        lock (_gate)
            return _current?.ManualRunId == runId ? _current
                : _last.Values.FirstOrDefault(x => x.ManualRunId == runId);
    }

    public Task<ManualRunStartResult> RunNowAsync(Guid taskId)
    {
        ManualRunState run;
        lock (_gate)
        {
            if (_stopping)
                return Task.FromResult(new ManualRunStartResult(false, null, "Application is stopping."));
            if (_deletedTimers.Contains(taskId))
                return Task.FromResult(new ManualRunStartResult(false, null, "Timer has been deleted."));
            if (_current is not null)
                return Task.FromResult(new ManualRunStartResult(false, null,
                    "Another manual run is still active (including cancellation/cleanup)."));
            run = new ManualRunState(Guid.NewGuid(), taskId, Guid.Empty, "", "",
                clock.GetUtcNow(), null, ManualRunStatus.Running);
            _current = run;
            _cancellation = new CancellationTokenSource();
            var source = _cancellation;
            // The task is tracked by this singleton and awaited on host shutdown.
            _execution = Task.Run(() => ExecuteAsync(run.ManualRunId, taskId, source.Token));
        }
        PublishChanged();
        return Task.FromResult(new ManualRunStartResult(true, run));
    }

    private async Task ExecuteAsync(Guid runId, Guid taskId, CancellationToken token)
    {
        using var timer = clock.CreateTimer(_ => RequestCancellation(runId, "Timeout"),
            null, options.Value.Timeout, System.Threading.Timeout.InfiniteTimeSpan);
        var status = ManualRunStatus.Failed;
        string? text = null, code = null, message = null, agentRunId = null;
        var truncated = false;
        var startedStamp = clock.GetTimestamp();
        try
        {
            ScheduledTaskDefinition task;
            // Serialize snapshot acceptance with JSON deletion and update, but never
            // keep the scheduling gate while executing LLM code.
            await schedulerState.Gate.WaitAsync(token);
            try
            {
                task = (await repository.GetAllAsync(token)).FirstOrDefault(t => t.Id == taskId)
                    ?? throw new KeyNotFoundException("Timer has been deleted.");
                ScheduledAgentConfigurationValidation.Validate(task.AgentExecution);
                var binding = task.AgentExecution!;
                UpdateCurrent(runId, state => state with
                {
                    TimerName = task.Name,
                    AgentId = binding.AgentId,
                    AgentName = binding.AgentId.ToString("D")
                });
            }
            finally { schedulerState.Gate.Release(); }

            var bindingSnapshot = task.AgentExecution!;
            var snapshot = new ScheduledAgentConfiguration
            {
                AgentId = bindingSnapshot.AgentId,
                Prompt = bindingSnapshot.Prompt,
                WorkspaceId = bindingSnapshot.WorkspaceId
            };
            await using var scope = scopes.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<IHeadlessAgentExecutor>();
            var result = await executor.ExecuteAsync(
                new HeadlessAgentRunRequest(snapshot.AgentId, snapshot.Prompt, snapshot.WorkspaceId), token);

            agentRunId = result.AgentRunId;
            UpdateCurrent(runId, state => state with { AgentName = result.AgentName, AgentRunId = result.AgentRunId });
            if (result.Succeeded)
            {
                status = ManualRunStatus.Completed;
                text = result.Text ?? string.Empty;
                if (text.Length > ScheduledTaskManualRunOptions.MaximumResultLength)
                {
                    text = text[..ScheduledTaskManualRunOptions.MaximumResultLength];
                    truncated = true;
                }
            }
            else
            {
                code = result.ErrorCode;
                message = result.ErrorMessage;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The reason is captured by the first cancellation request.
        }
        catch (KeyNotFoundException)
        {
            code = "timer_not_found";
            message = "Timer was deleted.";
        }
        catch (ArgumentException)
        {
            code = "invalid_agent_configuration";
            message = "Timer has an invalid Agent Execution configuration.";
        }
        catch (Exception ex)
        {
            code = "manual_run_failed";
            message = "Could not execute the Saved Agent. Check the server logs.";
            logger.LogError(
                "Manual agent run failed. ManualRunId={ManualRunId}, TimerId={TaskId}, ErrorType={ErrorType}",
                runId, taskId, ex.GetType().Name);
        }
        finally
        {
            // Async scope disposal has finished before this point and before the
            // global slot is released, including on cancellation and errors.
            lock (_gate)
            {
                if (_current?.ManualRunId == runId)
                {
                    var reason = _current.CancellationRequested;
                    status = reason == "Timeout" ? ManualRunStatus.TimedOut
                        : reason is not null ? ManualRunStatus.Canceled : status;
                    var finished = clock.GetUtcNow();
                    var terminal = _current with
                    {
                        Status = status,
                        FinishedUtc = finished,
                        AgentRunId = agentRunId ?? _current.AgentRunId,
                        FinalText = status == ManualRunStatus.Completed ? text : null,
                        IsTruncated = status == ManualRunStatus.Completed && truncated,
                        ErrorCode = status == ManualRunStatus.Failed ? code ?? "manual_run_failed" : null,
                        ErrorMessage = status == ManualRunStatus.Failed ? message ?? "Agent execution failed." : null
                    };
                    if (!_deletedDuringRun.Remove(runId))
                        _last[taskId] = terminal;
                    _current = null;
                    _cancellation?.Dispose();
                    _cancellation = null;
                    logger.LogInformation(
                        "Manual agent run finished. ManualRunId={ManualRunId}, AgentRunId={AgentRunId}, TimerId={TaskId}, Status={Status}, DurationMs={Duration}",
                        runId, terminal.AgentRunId, taskId, status, clock.GetElapsedTime(startedStamp).TotalMilliseconds);
                }
            }
            PublishChanged();
        }
    }

    public bool Cancel(Guid manualRunId) => RequestCancellation(manualRunId, "User");

    private bool RequestCancellation(Guid manualRunId, string reason)
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            if (_current?.ManualRunId != manualRunId || _current.CancellationRequested is not null)
                return false;
            _current = _current with { CancellationRequested = reason };
            source = _cancellation;
        }
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
        PublishChanged();
        return true;
    }

    public void TimerDeleted(Guid taskId)
    {
        Guid? active;
        lock (_gate)
        {
            _deletedTimers.Add(taskId);
            _last.Remove(taskId);
            active = _current?.ScheduledTaskId == taskId ? _current.ManualRunId : null;
            if (active.HasValue)
                _deletedDuringRun.Add(active.Value);
        }
        if (active.HasValue) RequestCancellation(active.Value, "TimerDeleted");
        PublishChanged();
    }

    public void TimerCreated(Guid taskId)
    {
        lock (_gate) _deletedTimers.Remove(taskId);
    }

    private void UpdateCurrent(Guid runId, Func<ManualRunState, ManualRunState> change)
    {
        lock (_gate)
            if (_current?.ManualRunId == runId) _current = change(_current);
        PublishChanged();
    }

    private void PublishChanged()
    {
        foreach (var handler in StateChanged?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { logger.LogDebug(ex, "Manual run state subscriber failed"); }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? task;
        Guid? runId;
        lock (_gate)
        {
            _stopping = true;
            runId = _current?.ManualRunId;
            task = _execution;
        }
        if (runId.HasValue) RequestCancellation(runId.Value, "HostShutdown");
        if (task is not null)
        {
            try { await task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}
