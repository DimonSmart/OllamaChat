namespace ChatClient.Domain.Models;

public sealed class VoiceInputStorageInfo
{
    public long TotalBytes { get; init; }

    public IReadOnlyList<VoiceInputDownloadedModel> DownloadedModels { get; init; } = [];
}
