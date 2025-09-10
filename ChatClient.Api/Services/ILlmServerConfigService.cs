using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public interface ILlmServerConfigService
{
    Task<IReadOnlyCollection<LlmServerConfig>> GetAllAsync();
    Task<LlmServerConfig?> GetByIdAsync(Guid serverId);
    Task CreateAsync(LlmServerConfig serverConfig);
    Task UpdateAsync(LlmServerConfig serverConfig);
    Task DeleteAsync(Guid serverId);
}
