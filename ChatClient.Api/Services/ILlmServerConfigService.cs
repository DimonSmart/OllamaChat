using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public interface ILlmServerConfigService
{
    Task<List<LlmServerConfig>> GetAllAsync();
    Task<LlmServerConfig?> GetByIdAsync(Guid id);
    Task<LlmServerConfig> CreateAsync(LlmServerConfig server);
    Task<LlmServerConfig> UpdateAsync(LlmServerConfig server);
    Task DeleteAsync(Guid id);
}
