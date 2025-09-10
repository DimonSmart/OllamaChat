using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IRagVectorIndexBackgroundService
{
    void RequestRebuild();
    RagVectorIndexStatus? GetCurrentStatus();
}
