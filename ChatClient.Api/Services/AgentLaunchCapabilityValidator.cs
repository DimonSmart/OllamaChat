using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class AgentLaunchCapabilityValidator(IModelCapabilityService modelCapabilityService)
    : IAgentLaunchCapabilityValidator
{
    public Task<bool> SupportsFunctionCallingAsync(
        ServerModel model,
        CancellationToken cancellationToken = default) =>
        modelCapabilityService.SupportsFunctionCallingAsync(model, cancellationToken);
}
