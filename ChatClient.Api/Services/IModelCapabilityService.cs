using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public interface IModelCapabilityService
{
    Task EnsureModelSupportedByServerAsync(ServerModel model, CancellationToken cancellationToken = default);

    Task<bool> SupportsFunctionCallingAsync(ServerModel model, CancellationToken cancellationToken = default);
}
