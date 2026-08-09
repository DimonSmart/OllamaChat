using ChatClient.Domain.Models;
namespace ChatClient.Application.Repositories;

public interface IAgentSkillsProfileRepository { Task<IReadOnlyCollection<AgentSkillsProfile>> GetAllAsync(CancellationToken cancellationToken = default); Task SaveAllAsync(List<AgentSkillsProfile> profiles, CancellationToken cancellationToken = default); }
