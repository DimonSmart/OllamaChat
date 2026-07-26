using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorIndexService(IUserSettingsService userSettings, IOllamaClientService ollama, IRagIndexMetadataStore metadata, RagVectorDataStore vectors, ILogger<RagVectorIndexService> logger) : IRagVectorIndexService
{
    private const string IngestionVersion = "data-ingestion-token-v1";
    [Obsolete]
    public async Task BuildIndexAsync(Guid agentId, string sourceFilePath, IProgress<RagVectorIndexStatus>? progress = null, CancellationToken cancellationToken = default, Guid serverId = default)
    {
        var text = await File.ReadAllTextAsync(sourceFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Source file is empty.");
        var settings = await userSettings.GetSettingsAsync(cancellationToken);
        var model = ModelSelectionHelper.GetEffectiveEmbeddingModel(settings.Embedding.Model, settings.DefaultModel, "RAG vector indexing", logger);
        var chunks = await ChunkAsync(Path.GetFileName(sourceFilePath), text, settings.Embedding, cancellationToken);
        var firstEmbedding = await ollama.GenerateEmbeddingAsync(chunks[0].Content, new ServerModel(serverId == Guid.Empty ? model.ServerId : serverId, model.ModelName), cancellationToken);
        var build = new RagIndexBuildMetadata(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), File.GetLastWriteTimeUtc(sourceFilePath), model.ModelName, firstEmbedding.Length, settings.Embedding.RagMaxTokensPerChunk, settings.Embedding.RagOverlapTokens, IngestionVersion, chunks.Count);
        var plan = await metadata.BeginIndexingAsync(agentId, Path.GetFileName(sourceFilePath), build, cancellationToken);
        var records = new List<RagChunkRecord>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var embedding = i == 0 ? firstEmbedding : await ollama.GenerateEmbeddingAsync(chunks[i].Content, new ServerModel(serverId == Guid.Empty ? model.ServerId : serverId, model.ModelName), cancellationToken);
            records.Add(chunks[i] with { AgentId = agentId.ToString("N"), Id = $"{agentId:N}:{Path.GetFileName(sourceFilePath)}:{i}", Embedding = embedding });
            progress?.Report(new RagVectorIndexStatus(agentId, Path.GetFileName(sourceFilePath), i + 1, chunks.Count));
        }
        await vectors.ReplaceFileAsync(agentId, Path.GetFileName(sourceFilePath), records, firstEmbedding.Length, cancellationToken);
        await metadata.CompleteIndexingAsync(agentId, Path.GetFileName(sourceFilePath), chunks.Count, cancellationToken);
    }

    private static async Task<List<RagChunkRecord>> ChunkAsync(string fileName, string text, EmbeddingSettings settings, CancellationToken cancellationToken)
    {
        var document = new IngestionDocument(fileName);
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(text));
        document.Sections.Add(section);
        var tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base", null, null);
        var chunker = new DocumentTokenChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = settings.RagMaxTokensPerChunk, OverlapTokens = settings.RagOverlapTokens });
        var result = new List<RagChunkRecord>();
        await foreach (var chunk in chunker.ProcessAsync(document, cancellationToken))
            result.Add(new RagChunkRecord { FileName = fileName, ChunkIndex = result.Count, Content = chunk.Content });
        return result;
    }
}
