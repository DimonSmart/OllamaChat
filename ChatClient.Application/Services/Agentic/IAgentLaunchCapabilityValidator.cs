using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IAgentLaunchCapabilityValidator
{
    Task<bool> SupportsFunctionCallingAsync(
        ServerModel model,
        CancellationToken cancellationToken = default);
}
