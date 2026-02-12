using ChatClient.Application.Helpers;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorIndexService(
    IUserSettingsService userSettings,
    IOllamaClientService ollamaClientService,
    IRagVectorIndexRepository repository,
    IRagVectorStore store,
    ILogger<RagVectorIndexService> logger) : IRagVectorIndexService
{
    [Obsolete]
    public async Task BuildIndexAsync(
        Guid agentId,
        string sourceFilePath,
        IProgress<RagVectorIndexStatus>? progress = null,
        CancellationToken cancellationToken = default,
        Guid serverId = default)
    {
        if (!repository.SourceExists(sourceFilePath))
            throw new FileNotFoundException($"Source file not found: {sourceFilePath}");

        var text = await repository.ReadSourceAsync(sourceFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Source file is empty.");

        var settings = await userSettings.GetSettingsAsync(cancellationToken);
        var embeddingModel = ModelSelectionHelper.GetEffectiveEmbeddingModel(
            settings.Embedding.Model,
            settings.DefaultModel,
            "RAG vector indexing",
            logger);

        var targetServerId = serverId == Guid.Empty ? embeddingModel.ServerId : serverId;
        var fileName = Path.GetFileName(sourceFilePath);

        var lineChunks = ChunkByWordCount(text, settings.Embedding.RagLineChunkSize, 0);
        var paragraphs = ChunkByWordCount(
            string.Join(Environment.NewLine, lineChunks),
            settings.Embedding.RagParagraphChunkSize,
            settings.Embedding.RagParagraphOverlap);

        var total = paragraphs.Count;
        logger.LogInformation("Building vector index for {File} with {Count} fragments", sourceFilePath, total);

        var entries = new List<RagVectorStoreEntry>(total);
        var nextLog = 10;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paragraph = paragraphs[i];

            var embedding = await ollamaClientService.GenerateEmbeddingAsync(
                paragraph,
                new ServerModel(targetServerId, embeddingModel.ModelName),
                cancellationToken);

            entries.Add(new RagVectorStoreEntry(fileName, i, paragraph, embedding));

            var processed = i + 1;
            progress?.Report(new RagVectorIndexStatus(agentId, fileName, processed, total));
            var percent = total == 0 ? 100 : processed * 100 / total;
            if (percent >= nextLog)
            {
                logger.LogInformation(
                    "Building index for {File}: {Percent}% ({Processed}/{Total})",
                    sourceFilePath,
                    percent,
                    processed,
                    total);
                nextLog += 10;
            }
        }

        await store.UpsertFileAsync(agentId, fileName, entries, cancellationToken);
        logger.LogInformation("Built vector index for {File} with {Count} fragments", sourceFilePath, total);
    }

    private static List<string> ChunkByWordCount(string text, int chunkSize, int overlap)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        int normalizedChunkSize = Math.Max(1, chunkSize);
        int normalizedOverlap = Math.Clamp(overlap, 0, normalizedChunkSize - 1);
        int step = Math.Max(1, normalizedChunkSize - normalizedOverlap);

        var words = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
        {
            return [];
        }

        List<string> chunks = [];
        for (int start = 0; start < words.Length; start += step)
        {
            int end = Math.Min(words.Length, start + normalizedChunkSize);
            var builder = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(words[i]);
            }

            var chunk = builder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }

            if (end == words.Length)
            {
                break;
            }
        }

        return chunks;
    }
}
