namespace ChatClient.Api.PlanningRuntime.Host;

public interface IPlanningSessionService
{
    PlanningSessionState State { get; }

    event Action? StateChanged;

    Task StartAsync(PlanningRunRequest request);

    Task CancelAsync();

    void Reset();
}
