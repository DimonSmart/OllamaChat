using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeSearchService(
    IKnowledgeStoreService stores,
    IUserSettingsService settings,
    IEmbeddingGeneratorResolver embeddingGeneratorResolver,
    IKnowledgeIndex knowledgeIndex) : IKnowledgeSearchService
{
    public async Task<bool> HasReadyContentAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) => (await stores.GetAllAsync(cancellationToken)).Any(IsRetrievable(ids));

    public async Task<RagSearchResponse> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Strategy))
            throw new ArgumentException("Invalid retrieval strategy.", nameof(request));
        if (request.MaxResults is < 1 or > 50)
            throw new ArgumentException("Maximum results must be between 1 and 50.", nameof(request));
        if (request.AdjacentChunkCount is < 0 or > 3)
            throw new ArgumentException("Adjacent chunk count must be between 0 and 3.", nameof(request));
        if (request.MaxRetrievedContextTokens is <= 0)
            throw new ArgumentException("Context token budget must be positive when specified.", nameof(request));
        var threshold = request.UseApplicationDefaultThreshold
            ? (await settings.GetSettingsAsync(cancellationToken)).Embedding.RagMinRelevanceScore
            : request.MinVectorRelevanceScore;
        var selected = (await stores.GetAllAsync(cancellationToken)).Where(IsRetrievable(request.KnowledgeStoreIds)).ToList();
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(request.Query))
            return new RagSearchResponse();

        var trimmedQuery = request.Query.Trim();
        var results = new List<RagSearchResult>();
        foreach (var group in selected.GroupBy(x => (x.Index.IndexedConfiguration!.ServerId, x.Index.IndexedConfiguration.Model)))
        {
            var profile = group.First().Index.IndexedConfiguration!;
            var generator = await embeddingGeneratorResolver.ResolveAsync(new ServerModel(profile.ServerId, profile.Model), cancellationToken);
            var generated = await generator.GenerateAsync([trimmedQuery], cancellationToken: cancellationToken);
            var embedding = generated[0].Vector;
            foreach (var store in group)
            {
                var found = await knowledgeIndex.SearchVectorAsync(new KnowledgeVectorSearchRequest
                {
                    Store = store,
                    QueryEmbedding = embedding,
                    MaxResults = request.MaxResults,
                    MinRelevanceScore = threshold
                }, cancellationToken);
                foreach (var result in found)
                {
                    result.KnowledgeStoreName = store.Name;
                    result.KnowledgeStoreId = store.Id;
                }
                results.AddRange(found);
            }
        }

        var ordered = results.OrderByDescending(x => x.Score).Take(request.MaxResults).ToList();
        return new RagSearchResponse { Total = ordered.Count, Results = ordered };
    }

    private static Func<KnowledgeStore, bool> IsRetrievable(IReadOnlyCollection<Guid> ids) => store => ids.Contains(store.Id) && store.Documents.Count > 0 && store.Index.State == KnowledgeStoreIndexState.Ready && store.Index.IndexedConfiguration?.Equals(store.Configuration) == true;
}
