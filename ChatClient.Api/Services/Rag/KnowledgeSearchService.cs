using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeSearchService(IKnowledgeStoreService stores, IUserSettingsService settings, IOllamaClientService ollama, KnowledgeVectorStore vectors) : IKnowledgeSearchService
{
    public async Task<bool> HasReadyContentAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => (await stores.GetAllAsync(ct)).Any(IsRetrievable(ids));

    public async Task<RagSearchResponse> SearchAsync(IReadOnlyCollection<Guid> ids, string query, int maxResults = 5, CancellationToken ct = default)
    {
        var threshold = (await settings.GetSettingsAsync(ct)).Embedding.RagMinRelevanceScore;

        return await SearchAsync(ids, query, maxResults, threshold, ct);
    }

    public async Task<RagSearchResponse> SearchAsync(IReadOnlyCollection<Guid> ids, string query, int maxResults, double? minRelevanceScore, CancellationToken ct = default)
    {
        var selected = (await stores.GetAllAsync(ct)).Where(IsRetrievable(ids)).ToList();
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(query))
            return new RagSearchResponse();

        var trimmedQuery = query.Trim();
        var results = new List<RagSearchResult>();
        foreach (var group in selected.GroupBy(x => (x.Index.IndexedConfiguration!.ServerId, x.Index.IndexedConfiguration.Model)))
        {
            var profile = group.First().Index.IndexedConfiguration!;
            var embedding = await ollama.GenerateEmbeddingAsync(trimmedQuery, new ServerModel(profile.ServerId, profile.Model), ct);
            foreach (var store in group)
            {
                var found = await vectors.SearchAsync(store, embedding, maxResults, minRelevanceScore, ct);
                foreach (var result in found)
                    result.KnowledgeStoreName = store.Name;
                results.AddRange(found);
            }
        }

        var ordered = results.OrderByDescending(x => x.Score).Take(maxResults).ToList();
        return new RagSearchResponse { Total = ordered.Count, Results = ordered };
    }

    private static Func<KnowledgeStore, bool> IsRetrievable(IReadOnlyCollection<Guid> ids) => store => ids.Contains(store.Id) && store.Documents.Count > 0 && store.Index.State == KnowledgeStoreIndexState.Ready && store.Index.IndexedConfiguration?.Equals(store.Configuration) == true;
}
