using Quartz;

namespace ChatClient.Api.Services.Scheduling;

/// <summary>Diagnostics only: never invokes an agent, workflow, MCP tool or user code.</summary>
public sealed class ScheduledTimerFiredJob(
    ScheduledTaskRuntimeState runtime,
    ITimerEventPublisher publisher,
    ILogger<ScheduledTimerFiredJob> logger,
    TimeProvider clock) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = context.JobDetail.JobDataMap;
            if (!Guid.TryParse(data.GetString("TimerId"), out var id) ||
                !long.TryParse(data.GetString("Generation"), out var generation))
                return;
            var actual = clock.GetUtcNow();
            var (valid, name) = await runtime.TryRecordAsync(id, generation, actual, cancellationToken);
            if (!valid) return;
            publisher.Publish(new TimerFiredEvent(id, name!, actual));
            logger.LogInformation("Scheduled timer fired: Id={Id}, Name={Name}, Scheduled={ScheduledUtc}, Actual={ActualUtc}",
                id, name, context.ScheduledFireTimeUtc, actual);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record scheduled timer firing");
        }
    }
}
