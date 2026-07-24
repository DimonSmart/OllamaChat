using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class AgentModeProviderProfileService(IAgentModeProviderProfileRepository repository) : IAgentModeProviderProfileService
{
    private const string AvailableModesPlaceholder = "{available_modes}";
    private const string CurrentModePlaceholder = "{current_mode}";
    private readonly IAgentModeProviderProfileRepository _repository = repository;

    public Task<IReadOnlyCollection<AgentModeProviderProfile>> GetAllAsync() => _repository.GetAllAsync();

    public async Task<AgentModeProviderProfile?> GetByIdAsync(Guid id) =>
        (await _repository.GetAllAsync()).FirstOrDefault(profile => profile.Id == id);

    public async Task CreateAsync(AgentModeProviderProfile profile)
    {
        var profiles = (await _repository.GetAllAsync()).ToList();
        NormalizeAndValidate(profile, profiles, null);
        var usedIds = profiles.Select(static item => item.Id).ToHashSet();
        while (profile.Id == Guid.Empty || !usedIds.Add(profile.Id))
            profile.Id = Guid.NewGuid();

        var now = DateTime.UtcNow;
        profile.CreatedAt = now;
        profile.UpdatedAt = now;
        profiles.Add(profile);
        await _repository.SaveAllAsync(profiles);
    }

    public async Task UpdateAsync(AgentModeProviderProfile profile)
    {
        var profiles = (await _repository.GetAllAsync()).ToList();
        var index = profiles.FindIndex(item => item.Id == profile.Id);
        if (index < 0)
            throw new KeyNotFoundException($"Agent Mode Provider profile with ID {profile.Id} not found");

        NormalizeAndValidate(profile, profiles, profile.Id);
        profile.CreatedAt = profiles[index].CreatedAt;
        profile.UpdatedAt = DateTime.UtcNow;
        profiles[index] = profile;
        await _repository.SaveAllAsync(profiles);
    }

    public async Task DeleteAsync(Guid id)
    {
        var profiles = (await _repository.GetAllAsync()).ToList();
        var profile = profiles.FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException($"Agent Mode Provider profile with ID {id} not found");
        profiles.Remove(profile);
        await _repository.SaveAllAsync(profiles);
    }

    private static void NormalizeAndValidate(AgentModeProviderProfile profile, IReadOnlyCollection<AgentModeProviderProfile> profiles, Guid? profileIdToExclude)
    {
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.Instructions = NormalizeOptionalText(profile.Instructions);
        profile.DefaultMode = NormalizeOptionalText(profile.DefaultMode);
        profile.Modes ??= [];

        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));
        if (profiles.Any(item => item.Id != profileIdToExclude && string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"An Agent Mode Provider profile named '{profile.Name}' already exists.", nameof(profile));
        if (profile.Modes.Count == 0)
            throw new ArgumentException("At least one mode is required.", nameof(profile));

        var modeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mode in profile.Modes)
        {
            mode.Name = mode.Name?.Trim() ?? string.Empty;
            mode.Instructions = mode.Instructions?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(mode.Name))
                throw new ArgumentException("Each mode requires a name.", nameof(profile));
            if (string.IsNullOrWhiteSpace(mode.Instructions))
                throw new ArgumentException($"Mode '{mode.Name}' requires instructions.", nameof(profile));
            if (!modeNames.Add(mode.Name))
                throw new ArgumentException($"A mode named '{mode.Name}' already exists.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.DefaultMode) || !profile.Modes.Any(mode => string.Equals(mode.Name, profile.DefaultMode, StringComparison.Ordinal)))
            throw new ArgumentException("Default mode must exactly match one of the configured modes.", nameof(profile));
        if (profile.Instructions is not null && (!profile.Instructions.Contains(AvailableModesPlaceholder, StringComparison.Ordinal) || !profile.Instructions.Contains(CurrentModePlaceholder, StringComparison.Ordinal)))
            throw new ArgumentException("Custom provider instructions must contain {available_modes} and {current_mode}.", nameof(profile));
    }

    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
