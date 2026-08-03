using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public static class RagProviderProfileValidator
{
    public static void Validate(RagProviderProfile profile, string? profileName = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var name = string.IsNullOrWhiteSpace(profileName) ? profile.Name : profileName;
        var prefix = string.IsNullOrWhiteSpace(name) ? "RAG Provider profile" : $"RAG Provider profile '{name}'";
        if (!Enum.IsDefined(profile.SearchMode))
            throw new ArgumentException($"{prefix} has an invalid search mode.");
        if (!Enum.IsDefined(profile.RetrievalStrategy))
            throw new ArgumentException($"{prefix} has an invalid retrieval strategy.");
        if (profile.MaxResults is < 1 or > 50)
            throw new ArgumentException($"{prefix} maximum results must be between 1 and 50.");
        if (profile.MinRelevanceScore is double score && (!double.IsFinite(score) || score is < -1 or > 1))
            throw new ArgumentException($"{prefix} minimum relevance score must be finite and between -1 and 1.");
        if (profile.RecentMessageMemoryLimit is < 0 or > 50)
            throw new ArgumentException($"{prefix} recent message memory limit must be between 0 and 50.");
        if (profile.MaxRetrievedContextTokens is < 256 or > 32768)
            throw new ArgumentException($"{prefix} context token budget must be between 256 and 32768.");
        if (profile.AdjacentChunkCount is < 0 or > 3)
            throw new ArgumentException($"{prefix} adjacent chunk count must be between 0 and 3.");
    }
}
