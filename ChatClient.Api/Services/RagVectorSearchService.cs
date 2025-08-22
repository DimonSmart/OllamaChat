using System.Text;
using System.Text.Json;

using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Memory;

namespace ChatClient.Api.Services;

public sealed class RagVectorSearchService(
    IMemoryStore store,
    IConfiguration configuration,
    ILogger<RagVectorSearchService> logger) : IRagVectorSearchService
{
    private readonly IMemoryStore _store = store;
    private readonly ILogger<RagVectorSearchService> _logger = logger;
    private readonly string _basePath =
        configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");

    public async Task<IReadOnlyList<RagSearchResult>> SearchAsync(
        Guid agentId,
        ReadOnlyMemory<float> queryVector,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        var collection = CollectionName(agentId);

        var matches = await _store.GetNearestMatchesAsync(
            collection: collection,
            embedding: queryVector,
            limit: Math.Max(maxResults * 8, maxResults),
            minRelevanceScore: 0.0,
            cancellationToken: ct);

        if (matches is null || matches.Count == 0)
            return [];

        var pieces = matches
            .Select(m => ToPiece(m.Item1, m.Item2))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        if (pieces.Count == 0)
            return [];

        var segments = MergeAdjacent(pieces)
            .OrderByDescending(s => s.Score)
            .Take(maxResults)
            .ToList();

        var filesRoot = Path.Combine(_basePath, agentId.ToString("D"), "files");
        var fileCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var results = new List<RagSearchResult>(segments.Count);

        foreach (var seg in segments)
        {
            if (!fileCache.TryGetValue(seg.File, out var bytes))
            {
                var path = Path.Combine(filesRoot, seg.File);
                if (!File.Exists(path))
                    continue;
                bytes = await File.ReadAllBytesAsync(path, ct);
                fileCache[seg.File] = bytes;
            }

            var start = (int)Math.Clamp(seg.StartOffset, 0, bytes.Length);
            var end = (int)Math.Clamp(seg.EndOffset, 0, bytes.Length);
            if (end <= start)
                continue;

            var text = Encoding.UTF8.GetString(bytes.AsSpan(start, end - start));
            results.Add(new RagSearchResult { FileName = seg.File, Content = text });
        }

        _logger.LogDebug("RAG search: agent={AgentId} pieces={Pieces} segments={Segments}", agentId, pieces.Count, segments.Count);
        return results;
    }

    private static string CollectionName(Guid id) => $"agent_{id:N}";

    private static Piece? ToPiece(MemoryRecord record, double score)
    {
        var metaJson = record.Metadata.AdditionalMetadata;
        if (string.IsNullOrWhiteSpace(metaJson))
            return null;

        try
        {
            var meta = JsonSerializer.Deserialize<Metadata>(metaJson);
            if (meta is null || string.IsNullOrEmpty(meta.file))
                return null;

            return new Piece(
                File: meta.file,
                Index: meta.index,
                Offset: meta.offset,
                Length: meta.length,
                Score: score);
        }
        catch
        {
            return null;
        }
    }

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
                    cur.EndOffset = Math.Max(cur.EndOffset, p.Offset + p.Length);
                    cur.StartOffset = Math.Min(cur.StartOffset, p.Offset);
                    cur.Score = Math.Max(cur.Score, p.Score);
                }
                else
                {
                    if (cur is not null)
                        result.Add(cur);
                    cur = new Segment(
                        File: p.File,
                        StartIndex: p.Index,
                        EndIndex: p.Index,
                        StartOffset: p.Offset,
                        EndOffset: p.Offset + p.Length,
                        Score: p.Score);
                }
            }

            if (cur is not null)
                result.Add(cur);
        }

        return result;
    }

    private sealed record Metadata(string file, int index, long offset, int length);
    private sealed record Piece(string File, int Index, long Offset, int Length, double Score);
    private sealed record Segment(string File, int StartIndex, int EndIndex, long StartOffset, long EndOffset, double Score);
}

