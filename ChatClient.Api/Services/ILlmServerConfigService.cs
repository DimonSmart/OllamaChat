using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public interface ILlmServerConfigService
{
    Task<List<LlmServerConfig>> GetAllAsync();
    Task<LlmServerConfig?> GetByIdAsync(Guid id);
    Task CreateAsync(LlmServerConfig serverConfig);
    Task UpdateAsync(LlmServerConfig serverConfig);
    Task DeleteAsync(Guid id);
}
