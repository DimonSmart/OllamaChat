namespace ChatClient.Domain.Models;

public sealed class VoiceInputTranscriptionProgress
{
    public int ProgressPercent { get; init; }

    public bool IsCompleted { get; init; }
}
