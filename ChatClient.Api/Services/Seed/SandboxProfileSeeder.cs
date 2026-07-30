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
        var provider = sandboxProviderRegistry.GetRequired(DockerSandboxProvider.ProviderType);
        var existingProfile = profiles.FirstOrDefault(profile =>
            profile.Id == DefaultDockerProfileId ||
            string.Equals(profile.Name, DefaultDockerProfileName, StringComparison.OrdinalIgnoreCase));

        if (existingProfile is not null)
        {
            if (IsLegacyDefaultProfile(existingProfile, provider))
            {
                existingProfile.Configuration = provider.DefaultConfiguration;
                existingProfile.UpdatedAt = DateTime.UtcNow;
                await repository.SaveAllAsync(profiles);
            }

            return;
        }

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

    private static bool IsLegacyDefaultProfile(SandboxProfile profile, ISandboxProvider provider)
    {
        if (!string.Equals(profile.ProviderType, provider.Type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var legacyConfiguration = provider.DefaultConfiguration.Replace(
            "network: bridge",
            "network: none",
            StringComparison.Ordinal);

        return string.Equals(
            NormalizeConfiguration(profile.Configuration),
            NormalizeConfiguration(legacyConfiguration),
            StringComparison.Ordinal);
    }

    private static string NormalizeConfiguration(string configuration) =>
        configuration.ReplaceLineEndings("\n").Trim();
}
