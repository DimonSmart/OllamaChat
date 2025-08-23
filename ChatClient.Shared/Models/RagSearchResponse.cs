namespace ChatClient.Shared.Models;

public class RagSearchResponse
{
    public int Total { get; init; }
    public IReadOnlyList<RagSearchResult> Results { get; init; } = [];
}
