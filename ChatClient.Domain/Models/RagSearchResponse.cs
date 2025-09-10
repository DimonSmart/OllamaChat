namespace ChatClient.Domain.Models;

public class RagSearchResponse
{
    public int Total { get; init; }
    public IReadOnlyList<RagSearchResult> Results { get; init; } = [];
}
