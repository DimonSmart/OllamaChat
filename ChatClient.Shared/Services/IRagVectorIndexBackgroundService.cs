using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IRagVectorIndexBackgroundService
{
    void RequestRebuild();
    RagVectorIndexStatus? GetCurrentStatus();
}
