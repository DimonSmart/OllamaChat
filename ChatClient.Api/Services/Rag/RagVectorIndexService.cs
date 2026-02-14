using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorIndexService(
    IUserSettingsService userSettings,
    IOllamaClientService ollamaClientService,
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
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException($"Source file not found: {sourceFilePath}");

        var text = await File.ReadAllTextAsync(sourceFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Source file is empty.");

        var settings = await userSettings.GetSettingsAsync(cancellationToken);
        var embeddingModel = ModelSelectionHelper.GetEffectiveEmbeddingModel(
            settings.Embedding.Model,
            settings.DefaultModel,
            "RAG vector indexing",
            logger);
        var embeddingModelName = RequireText(embeddingModel.ModelName, "Embedding model name");

        var targetServerId = serverId == Guid.Empty ? embeddingModel.ServerId : serverId;
        var fileName = Path.GetFileName(sourceFilePath);
        var sourceModifiedUtc = File.GetLastWriteTimeUtc(sourceFilePath);
        var sourceHash = ComputeSourceHash(text);

        var lineChunks = ChunkByWordCount(text, settings.Embedding.RagLineChunkSize, 0);
        var paragraphs = ChunkByWordCount(
            string.Join(Environment.NewLine, lineChunks),
            settings.Embedding.RagParagraphChunkSize,
            settings.Embedding.RagParagraphOverlap);

        var total = paragraphs.Count;
        var metadata = new RagVectorBuildMetadata(
            SourceHash: sourceHash,
            SourceModifiedUtc: sourceModifiedUtc,
            EmbeddingModel: embeddingModelName,
            LineChunkSize: settings.Embedding.RagLineChunkSize,
            ParagraphChunkSize: settings.Embedding.RagParagraphChunkSize,
            ParagraphOverlap: settings.Embedding.RagParagraphOverlap,
            TotalChunks: total);

        var resumePlan = await store.BeginIndexingAsync(agentId, fileName, metadata, cancellationToken: cancellationToken);
        var startIndex = resumePlan.StartIndex;

        logger.LogInformation("Building vector index for {File} with {Count} fragments", sourceFilePath, total);
        if (startIndex > 0)
        {
            logger.LogInformation(
                "Resuming vector index for {File} from fragment {Start}/{Total}",
                sourceFilePath,
                startIndex,
                total);
        }

        var nextLog = 10;
        progress?.Report(new RagVectorIndexStatus(agentId, fileName, startIndex, total));

        try
        {
            for (var i = startIndex; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var paragraph = paragraphs[i];

                var embedding = await ollamaClientService.GenerateEmbeddingAsync(
                    paragraph,
                    new ServerModel(targetServerId, embeddingModelName),
                    cancellationToken);

                await store.UpsertEntryAsync(
                    agentId,
                    fileName,
                    new RagVectorStoreEntry(fileName, i, paragraph, embedding),
                    processedChunks: i + 1,
                    totalChunks: total,
                    cancellationToken);

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

            await store.CompleteIndexingAsync(agentId, fileName, total, cancellationToken);
            logger.LogInformation("Built vector index for {File} with {Count} fragments", sourceFilePath, total);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Vector indexing was canceled for {File}", sourceFilePath);
            throw;
        }
        catch (Exception ex)
        {
            await store.MarkIndexingFailedAsync(agentId, fileName, ex.Message, CancellationToken.None);
            throw;
        }
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

    private static string ComputeSourceHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string RequireText(string? value, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"{fieldName} is not configured. Set it in Application Settings (Embedding model or Default model).");
    }
}
