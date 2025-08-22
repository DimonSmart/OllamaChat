using System.Text;
using System.Text.Json;

using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Memory;

namespace ChatClient.Api.Services;

public sealed class RagVectorSearchService(
    IMemoryStore store,
    ILogger<RagVectorSearchService> logger) : IRagVectorSearchService
{
    private readonly IMemoryStore _store = store;
    private readonly ILogger<RagVectorSearchService> _logger = logger;

    public async Task<IReadOnlyList<RagSearchResult>> SearchAsync(
        Guid agentId,
        ReadOnlyMemory<float> queryVector,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        var collection = CollectionName(agentId);

        var matches = new List<(MemoryRecord, double)>();
        await foreach (var match in _store.GetNearestMatchesAsync(
            collection,
            queryVector,
            Math.Max(maxResults * 8, maxResults),
            minRelevanceScore: 0.0,
            withEmbeddings: false,
            cancellationToken: ct))
        {
            matches.Add(match);
        }

        if (matches.Count == 0)
            return [];

        var pieces = matches
            .Select(m => ToPiece(m.Item1, m.Item2))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        if (pieces.Count == 0)
            return [];

        var results = MergeAdjacent(pieces)
            .OrderByDescending(s => s.Score)
            .Take(maxResults)
            .Select(s => new RagSearchResult { FileName = s.File, Content = s.Text.ToString() })
            .ToList();

        _logger.LogDebug("RAG search: agent={AgentId} pieces={Pieces} segments={Segments}", agentId, pieces.Count, results.Count);
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
                Text: meta.text,
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

    private sealed record Metadata(string file, int index, string text);
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

