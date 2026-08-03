using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public static class RagProviderRuntimeDefaults
{
    public const RagSearchMode SearchMode = RagSearchMode.Auto;
    public const RagRetrievalStrategy RetrievalStrategy = RagRetrievalStrategy.Hybrid;
    public const int MaxResults = 5;
    public const int RecentMessageMemoryLimit = 6;
    public const int MaxRetrievedContextTokens = 4000;
    public const int AdjacentChunkCount = 1;
    public const bool IncludeAssistantMessages = true;
    public const bool RequestCitations = true;
}
