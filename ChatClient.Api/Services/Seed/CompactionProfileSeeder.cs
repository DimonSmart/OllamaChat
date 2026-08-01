using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Seed;

public sealed class CompactionProfileSeeder(ICompactionProfileRepository repository)
{
    public static readonly Guid BalancedProfileId = Guid.Parse("cba898a8-922b-43fc-9340-34e271918418");
    public const string BalancedProfileName = "Balanced";

    public Task SeedAsync() => RestoreAsync(overwriteExisting: true);

    public Task RestoreAsync() => RestoreAsync(overwriteExisting: true);

    private async Task RestoreAsync(bool overwriteExisting)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var index = profiles.FindIndex(profile => profile.Id == BalancedProfileId);
        var balanced = CreateBalancedProfile();

        if (index < 0)
        {
            profiles.Add(balanced);
            await repository.SaveAllAsync(profiles);
        }
        else if (overwriteExisting)
        {
            balanced.Id = profiles[index].Id;
            balanced.CreatedAt = profiles[index].CreatedAt == default ? balanced.CreatedAt : profiles[index].CreatedAt;
            balanced.UpdatedAt = DateTime.UtcNow;
            profiles[index] = balanced;
            await repository.SaveAllAsync(profiles);
        }
    }

    public static CompactionProfile CreateBalancedProfile()
    {
        var now = DateTime.UtcNow;
        return new CompactionProfile
        {
            Id = BalancedProfileId,
            Name = BalancedProfileName,
            Kind = CompactionProfileKinds.ContextWindow,
            BudgetSource = CompactionBudgetSources.SelectedModel,
            ToolResultThreshold = .50,
            TruncationThreshold = .80,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
