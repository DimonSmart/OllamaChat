namespace ChatClient.Api.Services.Scheduling;

public interface ITimerEventPublisher
{
    event Action<TimerFiredEvent>? TimerFired;
    void Publish(TimerFiredEvent notification);
}
