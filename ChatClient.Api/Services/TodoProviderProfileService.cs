using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class TodoProviderProfileService(ITodoProviderProfileRepository repository) : ITodoProviderProfileService
{
    private readonly ITodoProviderProfileRepository _repository = repository;

    public Task<IReadOnlyCollection<TodoProviderProfile>> GetAllAsync() => _repository.GetAllAsync();

    public async Task<TodoProviderProfile?> GetByIdAsync(Guid id) =>
        (await _repository.GetAllAsync()).FirstOrDefault(profile => profile.Id == id);

    public async Task CreateAsync(TodoProviderProfile profile)
    {
        var profiles = (await _repository.GetAllAsync()).ToList();
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
        await _repository.SaveAllAsync(profiles);
    }

    public async Task UpdateAsync(TodoProviderProfile profile)
    {
        var profiles = (await _repository.GetAllAsync()).ToList();
        var index = profiles.FindIndex(item => item.Id == profile.Id);
        if (index < 0)
            throw new KeyNotFoundException($"Todo Provider profile with ID {profile.Id} not found");

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
            ?? throw new KeyNotFoundException($"Todo Provider profile with ID {id} not found");
        profiles.Remove(profile);
        await _repository.SaveAllAsync(profiles);
    }

    private static void NormalizeAndValidate(
        TodoProviderProfile profile,
        IReadOnlyCollection<TodoProviderProfile> profiles,
        Guid? profileIdToExclude)
    {
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.Instructions = NormalizeOptionalText(profile.Instructions);
        profile.TodoListMessageTemplate = NormalizeOptionalText(profile.TodoListMessageTemplate);

        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));

        if (profiles.Any(item => item.Id != profileIdToExclude &&
                                 string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A Todo Provider profile named '{profile.Name}' already exists.", nameof(profile));
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
