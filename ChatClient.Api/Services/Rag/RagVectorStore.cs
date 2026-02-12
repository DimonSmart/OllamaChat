using System.Text;
using System.Text.Json;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorStore(
    IConfiguration configuration,
    ILogger<RagVectorStore> logger) : IRagVectorStore
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _basePath = configuration["RagVectorStore:BasePath"] ?? Path.Combine("Data", "rag-vectors");

    public async Task UpsertFileAsync(
        Guid agentId,
        string fileName,
        IReadOnlyList<RagVectorStoreEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new RagVectorFilePayload
        {
            FileName = fileName,
            Entries = entries.Select(static entry => new RagVectorEntryPayload
            {
                Index = entry.Index,
                Text = entry.Text,
                Vector = entry.Vector
            }).ToList()
        };

        var filePath = GetFilePath(agentId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, payload, _jsonOptions, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task RemoveFileAsync(
        Guid agentId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = GetFilePath(agentId, fileName);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<bool> HasFileAsync(
        Guid agentId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = GetFilePath(agentId, fileName);
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var payload = await ReadFilePayloadAsync(filePath, cancellationToken);
            return payload?.Entries.Count > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read RAG vector payload for {FilePath}", filePath);
            return false;
        }
    }

    public async Task<IReadOnlyList<RagVectorStoreEntry>> ReadAgentEntriesAsync(
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        var folder = GetAgentFolder(agentId);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        List<RagVectorStoreEntry> entries = [];
        foreach (var filePath in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var payload = await ReadFilePayloadAsync(filePath, cancellationToken);
                if (payload is null || string.IsNullOrWhiteSpace(payload.FileName))
                {
                    continue;
                }

                entries.AddRange(payload.Entries.Select(entry =>
                    new RagVectorStoreEntry(
                        payload.FileName,
                        entry.Index,
                        entry.Text ?? string.Empty,
                        entry.Vector ?? [])));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load RAG vector file {FilePath}", filePath);
            }
        }

        return entries;
    }

    private async Task<RagVectorFilePayload?> ReadFilePayloadAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<RagVectorFilePayload>(stream, _jsonOptions, cancellationToken);
    }

    private string GetAgentFolder(Guid agentId) => Path.Combine(_basePath, agentId.ToString("N"));

    private string GetFilePath(Guid agentId, string fileName)
    {
        var folder = GetAgentFolder(agentId);
        return Path.Combine(folder, $"{EncodeFileName(fileName)}.json");
    }

    private static string EncodeFileName(string fileName)
    {
        var raw = Encoding.UTF8.GetBytes(fileName);
        return Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed class RagVectorFilePayload
    {
        public string FileName { get; set; } = string.Empty;
        public List<RagVectorEntryPayload> Entries { get; set; } = [];
    }

    private sealed class RagVectorEntryPayload
    {
        public int Index { get; set; }
        public string? Text { get; set; }
        public float[]? Vector { get; set; }
    }
}
