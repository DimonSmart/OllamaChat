namespace ChatClient.Api.Services.Scheduling;

/// <summary>Synchronous, best-effort in-process diagnostics publisher.</summary>
public sealed class TimerEventPublisher(ILogger<TimerEventPublisher> logger) : ITimerEventPublisher
{
    public event Action<TimerFiredEvent>? TimerFired;

    public void Publish(TimerFiredEvent notification)
    {
        var subscribers = TimerFired;
        if (subscribers is null) return;

        foreach (Action<TimerFiredEvent> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(notification);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Timer event subscriber failed for {TimerId}", notification.TimerId);
            }
        }
    }
}
