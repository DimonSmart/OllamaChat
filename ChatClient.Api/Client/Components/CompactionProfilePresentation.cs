using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Components;

public static class CompactionProfilePresentation
{
    public static string FormatPolicy(CompactionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return CompactionPolicySummary.FormatPolicy(profile);
    }
}
