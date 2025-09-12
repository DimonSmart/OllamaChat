using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Memory;
using System.Linq;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorSearchService(IMemoryStore store, ILogger<RagVectorSearchService> logger) : IRagVectorSearchService
{
    private readonly IMemoryStore _store = store;
    private readonly ILogger<RagVectorSearchService> _logger = logger;

    public async Task<RagSearchResponse> SearchAsync(
        Guid agentId,
        ReadOnlyMemory<float> queryVector,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        var collection = CollectionName(agentId);
        await _store.CreateCollectionAsync(collection, ct);

        var matches = new List<(MemoryRecord, double)>();
        await foreach (var result in _store.GetNearestMatchesAsync(collection, queryVector, maxResults * 8, withEmbeddings: false, cancellationToken: ct))
        {
            matches.Add(result);
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

    private static string CollectionName(Guid agentId) => $"agent_{agentId:N}";

    private static Piece ToPiece(MemoryRecord record, double score)
    {
        var key = record.Metadata.Id ?? record.Key;
        var idx = key.IndexOf('#');
        var file = idx >= 0 ? key[..idx] : key;
        var indexPart = idx >= 0 ? key[(idx + 1)..] : "0";
        var index = int.TryParse(indexPart, out var i) ? i : 0;
        return new Piece(file, index, record.Metadata.Text ?? string.Empty, score);
    }

    private static List<Segment> MergeAdjacent(IEnumerable<Piece> pieces)
    {
        List<Segment> result = [];

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

