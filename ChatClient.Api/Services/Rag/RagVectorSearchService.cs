using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorSearchService(
    IRagVectorStore store,
    ILogger<RagVectorSearchService> logger) : IRagVectorSearchService
{
    private readonly IRagVectorStore _store = store;
    private readonly ILogger<RagVectorSearchService> _logger = logger;

    public async Task<RagSearchResponse> SearchAsync(
        Guid agentId,
        ReadOnlyMemory<float> queryVector,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        var allEntries = await _store.ReadAgentEntriesAsync(agentId, ct);
        var matches = allEntries
            .Select(entry => new
            {
                Entry = entry,
                Score = Dot(queryVector.Span, entry.Vector)
            })
            .OrderByDescending(static x => x.Score)
            .Take(maxResults * 8)
            .ToList();

        if (matches.Count == 0)
            return new RagSearchResponse { Total = 0 };

        var pieces = matches
            .Select(static match => new Piece(
                match.Entry.FileName,
                match.Entry.Index,
                match.Entry.Text,
                match.Score))
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

    private static double Dot(ReadOnlySpan<float> a, IReadOnlyList<float> b)
    {
        int len = Math.Min(a.Length, b.Count);
        double sum = 0d;
        for (int i = 0; i < len; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
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

