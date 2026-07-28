using ChatClient.Api.Services.Sandbox;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Seed;

public sealed class SandboxProfileSeeder(
    ISandboxProfileRepository repository,
    ISandboxProviderRegistry sandboxProviderRegistry)
{
    private static readonly Guid DefaultDockerProfileId = Guid.Parse("6808d695-74fd-4bbf-a752-d4b1d74e7fd4");
    private const string DefaultDockerProfileName = ".NET 10 Small";

    public async Task SeedAsync()
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        if (profiles.Any(profile =>
                profile.Id == DefaultDockerProfileId ||
                string.Equals(profile.Name, DefaultDockerProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var provider = sandboxProviderRegistry.GetRequired(DockerSandboxProvider.ProviderType);
        var now = DateTime.UtcNow;
        profiles.Add(new SandboxProfile
        {
            Id = DefaultDockerProfileId,
            Name = DefaultDockerProfileName,
            ProviderType = provider.Type,
            Configuration = provider.DefaultConfiguration,
            CreatedAt = now,
            UpdatedAt = now
        });

        await repository.SaveAllAsync(profiles);
    }
}
