using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IVoiceInputService
{
    Task<VoiceInputSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<VoiceInputSettings> InitializeAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<VoiceInputStorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken = default);
    Task<VoiceInputSettings> ClearDownloadedModelsAsync(CancellationToken cancellationToken = default);
    Task<string> TranscribeAsync(Stream audioStream, CancellationToken cancellationToken = default);
}
