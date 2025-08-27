using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ChatClient.Api.Services;

public sealed class RagVectorSearchService(
    InMemoryVectorStore store,
    ILogger<RagVectorSearchService> logger,
    IConfiguration configuration) : IRagVectorSearchService
{
    private readonly InMemoryVectorStore _store = store;
    private readonly ILogger<RagVectorSearchService> _logger = logger;
    private readonly string _basePath = configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");
    private readonly ConcurrentDictionary<Guid, bool> _loaded = new();

    public async Task<RagSearchResponse> SearchAsync(
        Guid agentId,
        ReadOnlyMemory<float> queryVector,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        var collection = _store.GetCollection<string, RagVectorRecord>(CollectionName(agentId));
        await collection.EnsureCollectionExistsAsync(ct);
        await EnsureLoadedAsync(agentId, collection, ct);

        var options = new VectorSearchOptions<RagVectorRecord> { IncludeVectors = false };
        var matches = new List<(RagVectorRecord, double)>();
        await foreach (var result in collection.SearchAsync(queryVector, Math.Max(maxResults * 8, maxResults), options, ct))
        {
            if (result.Score is double score)
                matches.Add((result.Record, score));
        }

        if (matches.Count == 0)
            return new RagSearchResponse { Total = 0 };

        var pieces = matches
            .Select(m => ToPiece(m.Item1, m.Item2))
            .OrderByDescending(p => p.Score)
            .ToList();

        if (pieces.Count == 0)
            return new RagSearchResponse { Total = 0 };

        var segments = MergeAdjacent(pieces)
            .OrderByDescending(s => s.Score)
            .ToList();

        var results = segments
            .Take(maxResults)
            .Select(s => new RagSearchResult { FileName = s.File, Content = s.Text.ToString(), Score = s.Score })
            .ToList();

        _logger.LogDebug("RAG search: agent={AgentId} pieces={Pieces} segments={Segments}", agentId, pieces.Count, segments.Count);
        return new RagSearchResponse { Total = segments.Count, Results = results };
    }

    private async Task EnsureLoadedAsync(Guid agentId, VectorStoreCollection<string, RagVectorRecord> collection, CancellationToken ct)
    {
        if (_loaded.ContainsKey(agentId))
            return;
        var indexDir = Path.Combine(_basePath, agentId.ToString(), "index");
        if (!Directory.Exists(indexDir))
        {
            _loaded[agentId] = true;
            return;
        }

        foreach (var file in Directory.GetFiles(indexDir, "*.idx"))
        {
            RagVectorIndex? index;
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                index = JsonSerializer.Deserialize<RagVectorIndex>(json);
            }
            catch
            {
                continue;
            }
            if (index?.Fragments.Count is null or 0)
                continue;

            var records = new List<RagVectorRecord>();
            foreach (var fragment in index.Fragments)
            {
                records.Add(new RagVectorRecord
                {
                    Id = fragment.Id,
                    File = index.SourceFileName,
                    Index = ExtractIndex(fragment.Id),
                    Text = fragment.Text,
                    Embedding = new ReadOnlyMemory<float>(fragment.Vector)
                });
            }
            await collection.UpsertAsync(records, ct);
        }
        _loaded[agentId] = true;
    }

    private static int ExtractIndex(string id)
    {
        var hash = id.LastIndexOf('#');
        return hash >= 0 && int.TryParse(id[(hash + 1)..], out var idx) ? idx : 0;
    }

    private static string CollectionName(Guid id) => $"agent_{id:N}";

    private static Piece ToPiece(RagVectorRecord record, double score)
        => new(record.File, record.Index, record.Text, score);

    private static List<Segment> MergeAdjacent(IEnumerable<Piece> pieces)
    {
        var result = new List<Segment>();

        foreach (var group in pieces.GroupBy(p => p.File, StringComparer.OrdinalIgnoreCase))
        {
            Segment? cur = null;

            foreach (var p in group.OrderBy(p => p.Index))
            {
                if (cur is not null && p.Index == cur.EndIndex + 1)
                {
                    cur.EndIndex = p.Index;
                    cur.Text.Append(p.Text);
                    cur.Score = Math.Max(cur.Score, p.Score);
                }
                else
                {
                    if (cur is not null)
                        result.Add(cur);
                    cur = new Segment(
                        file: p.File,
                        startIndex: p.Index,
                        endIndex: p.Index,
                        text: new StringBuilder(p.Text),
                        score: p.Score);
                }
            }

            if (cur is not null)
                result.Add(cur);
        }

        return result;
    }

    private sealed record Piece(string File, int Index, string Text, double Score);
    private sealed class Segment
    {
        public string File { get; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public StringBuilder Text { get; }
        public double Score { get; set; }

        public Segment(string file, int startIndex, int endIndex, StringBuilder text, double score)
        {
            File = file;
            StartIndex = startIndex;
            EndIndex = endIndex;
            Text = text;
            Score = score;
        }
    }
}

