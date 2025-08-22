using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel.Memory;

namespace ChatClient.Api.Services;

/// <summary>
/// Simple in-memory implementation of <see cref="IMemoryStore"/> used for RAG search.
/// </summary>
public sealed class VolatileMemoryStore : IMemoryStore
{
    private readonly ConcurrentDictionary<string, List<MemoryRecord>> _collections = new(StringComparer.OrdinalIgnoreCase);

    public Task CreateCollectionAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        _collections.TryAdd(collectionName, []);
        return Task.CompletedTask;
    }

    public Task DeleteCollectionAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        _collections.TryRemove(collectionName, out _);
        return Task.CompletedTask;
    }

    public Task<bool> DoesCollectionExistAsync(string collectionName, CancellationToken cancellationToken = default)
        => Task.FromResult(_collections.ContainsKey(collectionName));

    public async IAsyncEnumerable<string> GetCollectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in _collections.Keys)
            yield return key;
    }

    public Task<string> UpsertAsync(string collectionName, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var list = _collections.GetOrAdd(collectionName, _ => []);
        list.RemoveAll(r => r.Metadata.Id == record.Metadata.Id);
        list.Add(record);
        return Task.FromResult(record.Metadata.Id);
    }

    public async IAsyncEnumerable<string> UpsertBatchAsync(
        string collectionName,
        IEnumerable<MemoryRecord> records,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
            yield return await UpsertAsync(collectionName, record, cancellationToken);
    }

    public Task RemoveAsync(string collectionName, string key, CancellationToken cancellationToken = default)
    {
        if (_collections.TryGetValue(collectionName, out var list))
            list.RemoveAll(r => r.Metadata.Id == key);
        return Task.CompletedTask;
    }

    public async Task RemoveBatchAsync(string collectionName, IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
            await RemoveAsync(collectionName, key, cancellationToken);
    }

    public Task<MemoryRecord?> GetAsync(
        string collectionName,
        string key,
        bool withEmbeddings = false,
        CancellationToken cancellationToken = default)
    {
        if (_collections.TryGetValue(collectionName, out var list))
        {
            var rec = list.FirstOrDefault(r => r.Metadata.Id == key);
            if (rec is not null && !withEmbeddings)
                return Task.FromResult<MemoryRecord?>(new MemoryRecord(rec.Metadata, default, rec.Key, null));
            if (rec is not null)
                return Task.FromResult<MemoryRecord?>(rec);
        }
        return Task.FromResult<MemoryRecord?>(null);
    }

    public async IAsyncEnumerable<MemoryRecord> GetBatchAsync(
        string collectionName,
        IEnumerable<string> keys,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            var rec = await GetAsync(collectionName, key, withEmbeddings, cancellationToken);
            if (rec is not null)
                yield return rec;
        }
    }

    public async IAsyncEnumerable<(MemoryRecord, double)> GetNearestMatchesAsync(
        string collectionName,
        ReadOnlyMemory<float> embedding,
        int limit,
        double minRelevanceScore = 0,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_collections.TryGetValue(collectionName, out var list) || list.Count == 0)
            yield break;

        var scored = list
            .Select(r => (Record: r, Score: CosineSimilarity(embedding.Span, r.Embedding.Span)))
            .Where(r => r.Score >= minRelevanceScore)
            .OrderByDescending(r => r.Score)
            .Take(limit);

        foreach (var item in scored)
        {
            var rec = item.Record;
            if (!withEmbeddings)
                rec = new MemoryRecord(rec.Metadata, default, rec.Key, null);
            yield return (rec, item.Score);
        }
    }

    public async Task<(MemoryRecord, double)?> GetNearestMatchAsync(
        string collectionName,
        ReadOnlyMemory<float> embedding,
        double minRelevanceScore = 0,
        bool withEmbeddings = false,
        CancellationToken cancellationToken = default)
    {
        await foreach (var item in GetNearestMatchesAsync(collectionName, embedding, 1, minRelevanceScore, withEmbeddings, cancellationToken))
            return item;
        return null;
    }

    private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, magA = 0, magB = 0;
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var ai = a[i];
            var bi = b[i];
            dot += ai * bi;
            magA += ai * ai;
            magB += bi * bi;
        }
        if (magA == 0 || magB == 0)
            return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
