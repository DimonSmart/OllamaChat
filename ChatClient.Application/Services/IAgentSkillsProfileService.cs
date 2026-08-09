using ChatClient.Domain.Models;
namespace ChatClient.Application.Services;

public interface IAgentSkillsProfileService { Task<IReadOnlyCollection<AgentSkillsProfile>> GetAllAsync(); Task<AgentSkillsProfile?> GetByIdAsync(Guid id); Task CreateAsync(AgentSkillsProfile profile); Task UpdateAsync(AgentSkillsProfile profile); Task DeleteAsync(Guid id); }
