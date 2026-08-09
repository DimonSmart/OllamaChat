using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
namespace ChatClient.Api.Services;

public sealed class AgentSkillsProfileService(IAgentSkillsProfileRepository repository) : IAgentSkillsProfileService
{
    public Task<IReadOnlyCollection<AgentSkillsProfile>> GetAllAsync() => repository.GetAllAsync();
    public async Task<AgentSkillsProfile?> GetByIdAsync(Guid id) => (await repository.GetAllAsync()).FirstOrDefault(x => x.Id == id);
    public async Task CreateAsync(AgentSkillsProfile profile) { var all = (await repository.GetAllAsync()).ToList(); Normalize(profile, all, null); while (profile.Id == Guid.Empty || all.Any(x => x.Id == profile.Id)) profile.Id = Guid.NewGuid(); profile.CreatedAt = profile.UpdatedAt = DateTime.UtcNow; all.Add(profile); await repository.SaveAllAsync(all); }
    public async Task UpdateAsync(AgentSkillsProfile profile) { var all = (await repository.GetAllAsync()).ToList(); var index = all.FindIndex(x => x.Id == profile.Id); if (index < 0) throw new KeyNotFoundException($"Skills Provider profile with ID {profile.Id} not found"); Normalize(profile, all, profile.Id); profile.CreatedAt = all[index].CreatedAt; profile.UpdatedAt = DateTime.UtcNow; all[index] = profile; await repository.SaveAllAsync(all); }
    public async Task DeleteAsync(Guid id) { var all = (await repository.GetAllAsync()).ToList(); var profile = all.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Skills Provider profile with ID {id} not found"); all.Remove(profile); await repository.SaveAllAsync(all); }
    private static void Normalize(AgentSkillsProfile profile, IReadOnlyCollection<AgentSkillsProfile> all, Guid? exclude)
    {
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));
        if (all.Any(x => x.Id != exclude && string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A Skills Provider profile named '{profile.Name}' already exists.", nameof(profile));
        profile.FileSources ??= [];
        foreach (var source in profile.FileSources)
        { source.Directory = source.Directory?.Trim() ?? string.Empty; source.Patterns = (source.Patterns ?? []).Select(x => x?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList(); if (string.IsNullOrWhiteSpace(source.Directory)) throw new ArgumentException("Skill source directory is required.", nameof(profile)); if (source.Patterns.Count == 0) throw new ArgumentException("Each skill source requires at least one pattern.", nameof(profile)); if (!Path.IsPathFullyQualified(source.Directory) && source.Directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x == "..")) throw new ArgumentException("Relative skill source directories cannot leave the workspace.", nameof(profile)); if (source.Patterns.Any(Path.IsPathFullyQualified)) throw new ArgumentException("Skill patterns must be relative.", nameof(profile)); }
    }
}
