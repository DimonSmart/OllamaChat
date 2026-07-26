using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeSearchService(IKnowledgeStoreService stores, IUserSettingsService settings, IOllamaClientService ollama, KnowledgeVectorStore vectors, ILogger<KnowledgeSearchService> logger) : IKnowledgeSearchService
{
    public async Task<bool> HasReadyContentAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => (await stores.GetAllAsync(ct)).Any(x => ids.Contains(x.Id) && x.Index.State == KnowledgeStoreIndexState.Ready && x.Documents.Count > 0 && x.Index.IndexedConfiguration?.Equals(x.Configuration) == true);
    public async Task<RagSearchResponse> SearchAsync(IReadOnlyCollection<Guid> ids, string query, int maxResults = 5, CancellationToken ct = default)
    {
        var selected = (await stores.GetAllAsync(ct)).Where(x => ids.Contains(x.Id) && x.Index.State == KnowledgeStoreIndexState.Ready && x.Index.IndexedConfiguration?.Equals(x.Configuration) == true).ToList();
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(query))
            return new RagSearchResponse();
        var app = await settings.GetSettingsAsync(ct);
        var model = ModelSelectionHelper.GetEffectiveEmbeddingModel(app.Embedding.Model, app.DefaultModel, "Knowledge search", logger);
        var embedding = await ollama.GenerateEmbeddingAsync(query.Trim(), new ServerModel(model.ServerId, model.ModelName), ct);
        var results = new List<RagSearchResult>();
        foreach (var store in selected)
            results.AddRange(await vectors.SearchAsync(store, embedding, maxResults, app.Embedding.RagMinRelevanceScore, ct));
        var ordered = results.OrderByDescending(x => x.Score).Take(maxResults).ToList();
        return new RagSearchResponse { Total = ordered.Count, Results = ordered };
    }
}
