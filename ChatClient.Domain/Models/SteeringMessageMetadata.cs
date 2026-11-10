namespace ChatClient.Domain.Models;

public enum SteeringDeliveryStatus
{
    Pending,
    Delivered,
    NotDelivered
}

public sealed class SteeringMessageMetadata
{
    public Guid SteeringId { get; set; }
    public Guid RunId { get; set; }
    public long Sequence { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public SteeringDeliveryStatus Status { get; set; } = SteeringDeliveryStatus.Pending;
    public string? Reason { get; set; }

    public SteeringMessageMetadata Clone() => new()
    {
        SteeringId = SteeringId,
        RunId = RunId,
        Sequence = Sequence,
        SubmittedAtUtc = SubmittedAtUtc,
        Status = Status,
        Reason = Reason
    };
}
