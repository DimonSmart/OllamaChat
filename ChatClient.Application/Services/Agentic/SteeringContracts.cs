namespace ChatClient.Application.Services.Agentic;

public sealed record SteeringAvailability(bool CanSteer, Guid? RunId)
{
    public static SteeringAvailability Unavailable { get; } = new(false, null);
}

public sealed record SteeringSubmissionResult(bool Accepted, Guid? SteeringId, string? Error)
{
    public static SteeringSubmissionResult Rejected(string reason) => new(false, null, reason);
    public static SteeringSubmissionResult Success(Guid id) => new(true, id, null);
}
