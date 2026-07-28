using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class SandboxProfileService(
    ISandboxProfileRepository repository,
    ISandboxProviderRegistry sandboxProviderRegistry) : ISandboxProfileService
{
    public Task<IReadOnlyCollection<SandboxProfile>> GetAllAsync() => repository.GetAllAsync();

    public async Task<SandboxProfile?> GetByIdAsync(Guid id) =>
        (await repository.GetAllAsync()).FirstOrDefault(profile => profile.Id == id);

    public async Task CreateAsync(SandboxProfile profile)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        NormalizeAndValidate(profile, profiles, null);
        var usedIds = profiles.Select(static item => item.Id).ToHashSet();
        while (profile.Id == Guid.Empty || !usedIds.Add(profile.Id))
        {
            profile.Id = Guid.NewGuid();
        }

        var now = DateTime.UtcNow;
        profile.CreatedAt = now;
        profile.UpdatedAt = now;
        profiles.Add(profile);
        await repository.SaveAllAsync(profiles);
    }

    public async Task UpdateAsync(SandboxProfile profile)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var index = profiles.FindIndex(item => item.Id == profile.Id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Sandbox profile with ID {profile.Id} not found");
        }

        NormalizeAndValidate(profile, profiles, profile.Id);
        profile.CreatedAt = profiles[index].CreatedAt;
        profile.UpdatedAt = DateTime.UtcNow;
        profiles[index] = profile;
        await repository.SaveAllAsync(profiles);
    }

    public async Task DeleteAsync(Guid id)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var profile = profiles.FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException($"Sandbox profile with ID {id} not found");
        profiles.Remove(profile);
        await repository.SaveAllAsync(profiles);
    }

    private void NormalizeAndValidate(
        SandboxProfile profile,
        IReadOnlyCollection<SandboxProfile> profiles,
        Guid? profileIdToExclude)
    {
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.ProviderType = profile.ProviderType?.Trim() ?? string.Empty;
        profile.Configuration = profile.Configuration?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Profile name is required.", nameof(profile));
        }

        if (profiles.Any(item => item.Id != profileIdToExclude &&
                                 string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A Sandbox profile named '{profile.Name}' already exists.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.ProviderType))
        {
            throw new ArgumentException("Provider type is required.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Configuration))
        {
            throw new ArgumentException("Sandbox configuration is required.", nameof(profile));
        }

        var provider = sandboxProviderRegistry.GetRequired(profile.ProviderType);
        var definition = provider.ParseDefinition(profile.Configuration);
        var validation = provider.ValidateDefinition(definition);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, validation.Errors), nameof(profile));
        }
    }
}
