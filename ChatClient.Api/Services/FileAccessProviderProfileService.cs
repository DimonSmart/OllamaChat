using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class FileAccessProviderProfileService(IFileAccessProviderProfileRepository repository) : IFileAccessProviderProfileService
{
    public Task<IReadOnlyCollection<FileAccessProviderProfile>> GetAllAsync() => repository.GetAllAsync();
    public async Task<FileAccessProviderProfile?> GetByIdAsync(Guid id) => (await repository.GetAllAsync()).FirstOrDefault(x => x.Id == id);

    public async Task CreateAsync(FileAccessProviderProfile profile)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        NormalizeAndValidate(profile, profiles, null);
        while (profile.Id == Guid.Empty || profiles.Any(x => x.Id == profile.Id))
            profile.Id = Guid.NewGuid();
        profile.CreatedAt = profile.UpdatedAt = DateTime.UtcNow;
        profiles.Add(profile);
        await repository.SaveAllAsync(profiles);
    }

    public async Task UpdateAsync(FileAccessProviderProfile profile)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var index = profiles.FindIndex(x => x.Id == profile.Id);
        if (index < 0)
            throw new KeyNotFoundException($"File Access Provider profile with ID {profile.Id} not found");
        NormalizeAndValidate(profile, profiles, profile.Id);
        profile.CreatedAt = profiles[index].CreatedAt;
        profile.UpdatedAt = DateTime.UtcNow;
        profiles[index] = profile;
        await repository.SaveAllAsync(profiles);
    }

    public async Task DeleteAsync(Guid id)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var profile = profiles.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"File Access Provider profile with ID {id} not found");
        profiles.Remove(profile);
        await repository.SaveAllAsync(profiles);
    }

    private static void NormalizeAndValidate(FileAccessProviderProfile profile, IReadOnlyCollection<FileAccessProviderProfile> profiles, Guid? exclude)
    {
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.Instructions = string.IsNullOrWhiteSpace(profile.Instructions) ? null : profile.Instructions.Trim();
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));
        if (profiles.Any(x => x.Id != exclude && string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A File Access Provider profile named '{profile.Name}' already exists.", nameof(profile));
        if (!Enum.IsDefined(profile.AccessMode))
            throw new ArgumentException("Invalid file access mode.", nameof(profile));
    }
}
