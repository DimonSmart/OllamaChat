using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Linq;

using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Api.Services;

public sealed class RagVectorSearchService(
    IConfiguration configuration,
    ILogger<RagVectorSearchService> logger) : IRagVectorSearchService
{
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<RagVectorSearchService> _logger = logger;
    private readonly ConcurrentDictionary<Guid, Task<AgentIndex>> _cache = new();

    public async Task<IReadOnlyList<RagSearchResult>> SearchAsync(Guid agentId, ReadOnlyMemory<float> queryVector, int maxResults = 5, CancellationToken cancellationToken = default)
    {
        var index = await LoadAgentIndexAsync(agentId, cancellationToken);
        if (index.Fragments.Count == 0)
            return Array.Empty<RagSearchResult>();

        var scored = index.Fragments
            .Select(f => new { Fragment = f, Score = CosineSimilarity(queryVector.Span, f.Vector) })
            .OrderByDescending(s => s.Score)
            .Take(maxResults * 8)
            .ToList();

        var segments = new List<Segment>();
        foreach (var s in scored)
        {
            var f = s.Fragment;
            var seg = segments.FirstOrDefault(x => x.FileName == f.FileName && (f.Index == x.EndIndex + 1 || f.Index == x.StartIndex - 1));
            if (seg != null)
            {
                if (f.Index == seg.EndIndex + 1)
                    seg.EndIndex = f.Index;
                else
                    seg.StartIndex = f.Index;
                seg.StartOffset = Math.Min(seg.StartOffset, f.Offset);
                seg.EndOffset = Math.Max(seg.EndOffset, f.Offset + f.Length);
                seg.Score = Math.Max(seg.Score, s.Score);
            }
            else
            {
                segments.Add(new Segment
                {
                    FileName = f.FileName,
                    StartIndex = f.Index,
                    EndIndex = f.Index,
                    StartOffset = f.Offset,
                    EndOffset = f.Offset + f.Length,
                    Score = s.Score
                });
            }
        }

        var top = segments
            .OrderByDescending(s => s.Score)
            .Take(maxResults)
            .ToList();

        var results = new List<RagSearchResult>();
        foreach (var seg in top)
        {
            var path = Path.Combine(index.AgentFolder, "files", seg.FileName);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var slice = bytes[(int)seg.StartOffset..(int)seg.EndOffset];
            var text = Encoding.UTF8.GetString(slice);
            results.Add(new RagSearchResult { FileName = seg.FileName, Content = text });
        }

        return results;
    }

    private Task<AgentIndex> LoadAgentIndexAsync(Guid agentId, CancellationToken token)
        => _cache.GetOrAdd(agentId, id => LoadAgentIndexInternalAsync(id, token));

    private async Task<AgentIndex> LoadAgentIndexInternalAsync(Guid agentId, CancellationToken token)
    {
        var basePath = _configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");
        var agentFolder = Path.Combine(basePath, agentId.ToString());
        var indexFolder = Path.Combine(agentFolder, "index");
        var fragments = new List<FragmentInfo>();
        if (Directory.Exists(indexFolder))
        {
            foreach (var file in Directory.EnumerateFiles(indexFolder, "*.idx"))
            {
                var json = await File.ReadAllTextAsync(file, token);
                var data = JsonSerializer.Deserialize<RagVectorIndex>(json);
                if (data == null)
                    continue;
                foreach (var frag in data.Fragments)
                {
                    var hash = frag.Id.LastIndexOf('#');
                    var index = hash >= 0 && int.TryParse(frag.Id[(hash + 1)..], out var idx) ? idx : 0;
                    fragments.Add(new FragmentInfo
                    {
                        FileName = data.SourceFileName,
                        Index = index,
                        Offset = frag.Offset,
                        Length = frag.Length,
                        Vector = frag.Vector
                    });
                }
            }
        }
        _logger.LogInformation("Loaded {Count} fragments for {AgentId}", fragments.Count, agentId);
        return new AgentIndex(agentFolder, fragments);
    }

    private static double CosineSimilarity(ReadOnlySpan<float> a, float[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0 || magB == 0)
            return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private sealed class AgentIndex(string folder, List<FragmentInfo> fragments)
    {
        public string AgentFolder { get; } = folder;
        public List<FragmentInfo> Fragments { get; } = fragments;
    }

    private sealed class FragmentInfo
    {
        public string FileName { get; set; } = string.Empty;
        public int Index { get; set; }
        public long Offset { get; set; }
        public int Length { get; set; }
        public float[] Vector { get; set; } = Array.Empty<float>();
    }

    private sealed class Segment
    {
        public string FileName { get; set; } = string.Empty;
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public long StartOffset { get; set; }
        public long EndOffset { get; set; }
        public double Score { get; set; }
    }
}
