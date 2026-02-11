using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public interface IModelCapabilityService
{
    Task<bool> SupportsFunctionCallingAsync(ServerModel model, CancellationToken cancellationToken = default);
}
