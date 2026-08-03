using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class RagProviderProfileService(
    IRagProviderProfileRepository repository,
    IAgentTemplateRepository agentTemplateRepository) : IRagProviderProfileService
{
    public Task<IReadOnlyCollection<RagProviderProfile>> GetAllAsync() => repository.GetAllAsync();
    public async Task<RagProviderProfile?> GetByIdAsync(Guid id) => (await repository.GetAllAsync()).FirstOrDefault(x => x.Id == id);

    public async Task CreateAsync(RagProviderProfile profile)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        NormalizeAndValidate(profile, profiles, null);
        while (profile.Id == Guid.Empty || profiles.Any(x => x.Id == profile.Id))
            profile.Id = Guid.NewGuid();
        profile.CreatedAt = profile.UpdatedAt = DateTime.UtcNow;
        profiles.Add(profile);
        await repository.SaveAllAsync(profiles);
    }

    public async Task UpdateAsync(RagProviderProfile profile)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var index = profiles.FindIndex(x => x.Id == profile.Id);
        if (index < 0)
            throw new KeyNotFoundException($"RAG Provider profile with ID {profile.Id} not found");
        NormalizeAndValidate(profile, profiles, profile.Id);
        profile.CreatedAt = profiles[index].CreatedAt;
        profile.UpdatedAt = DateTime.UtcNow;
        profiles[index] = profile;
        await repository.SaveAllAsync(profiles);
    }

    public async Task DeleteAsync(Guid id)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var profile = profiles.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"RAG Provider profile with ID {id} not found");
        var users = (await agentTemplateRepository.GetAllAsync()).Where(x => x.RagProviderProfileId == id).Select(x => x.AgentName).ToList();
        if (users.Count != 0)
            throw new InvalidOperationException($"RAG Provider profile '{profile.Name}' is used by saved agents: {string.Join(", ", users)}.");
        profiles.Remove(profile);
        await repository.SaveAllAsync(profiles);
    }

    private static void NormalizeAndValidate(RagProviderProfile profile, IReadOnlyCollection<RagProviderProfile> profiles, Guid? excludedId)
    {
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.FunctionToolDescription = NormalizeOptional(profile.FunctionToolDescription);
        profile.AdditionalContextInstructions = NormalizeOptional(profile.AdditionalContextInstructions);
        profile.CitationsPrompt = NormalizeOptional(profile.CitationsPrompt);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));
        if (profiles.Any(x => x.Id != excludedId && string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A RAG Provider profile named '{profile.Name}' already exists.", nameof(profile));
        RagProviderProfileValidator.Validate(profile);
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
