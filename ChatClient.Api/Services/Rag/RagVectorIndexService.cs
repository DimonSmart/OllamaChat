#pragma warning disable SKEXP0050, SKEXP0070
using ChatClient.Application.Helpers;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Text;
using System.Linq;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorIndexService(
    IUserSettingsService userSettings,
    ILlmServerConfigService llmServerConfigService,
    IRagVectorIndexRepository repository,
    IMemoryStore store,
    ILogger<RagVectorIndexService> logger) : IRagVectorIndexService
{
    private readonly IRagVectorIndexRepository _repository = repository;
    private readonly IMemoryStore _store = store;

    public async Task BuildIndexAsync(Guid agentId, string sourceFilePath, IProgress<RagVectorIndexStatus>? progress = null, CancellationToken cancellationToken = default, Guid serverId = default)
    {
        if (!_repository.SourceExists(sourceFilePath))
            throw new FileNotFoundException($"Source file not found: {sourceFilePath}");

        var text = await _repository.ReadSourceAsync(sourceFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Source file is empty.");

        var model = await GetModelAsync();
        var targetServer = serverId == Guid.Empty ? model.ServerId : serverId;
        var baseUrl = await GetBaseUrlAsync(targetServer);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Base URL cannot be empty for embedding service");

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddLogging();
        builder.AddOllamaTextEmbeddingGeneration(model.ModelName, new Uri(baseUrl.Trim()));
        var kernel = builder.Build();

        var embeddingService = kernel.GetRequiredService<ITextEmbeddingGenerationService>();
        var memory = new MemoryBuilder()
            .WithMemoryStore(_store)
            .WithTextEmbeddingGeneration(embeddingService)
            .Build();

        var settings = await userSettings.GetSettingsAsync();
        var maxTokensPerLine = settings.Embedding.RagLineChunkSize;
        var maxTokensPerParagraph = settings.Embedding.RagParagraphChunkSize;
        var paragraphOverlap = settings.Embedding.RagParagraphOverlap;
        var lines = TextChunker.SplitPlainTextLines(text, maxTokensPerLine, null);
        var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, maxTokensPerParagraph, paragraphOverlap, string.Empty, null).ToList();
        var total = paragraphs.Count;

        logger.LogInformation("Building index for {File} with {Count} fragments", sourceFilePath, total);

        var collection = CollectionName(agentId);
        await _store.CreateCollectionAsync(collection, cancellationToken);
        for (var i = 0; ; i++)
        {
            var key = Key(Path.GetFileName(sourceFilePath), i);
            var existing = await _store.GetAsync(collection, key, cancellationToken: cancellationToken);
            if (existing is null)
                break;
            await _store.RemoveAsync(collection, key, cancellationToken);
        }

        var nextLog = 10;
        for (var i = 0; i < total; i++)
        {
            var paragraph = paragraphs[i];
            var key = Key(Path.GetFileName(sourceFilePath), i);
            await memory.SaveInformationAsync(collection, paragraph, key, cancellationToken: cancellationToken);

            var processed = i + 1;
            progress?.Report(new(agentId, Path.GetFileName(sourceFilePath), processed, total));
            var percent = processed * 100 / total;
            if (percent >= nextLog)
            {
                logger.LogInformation("Building index for {File}: {Percent}% ({Processed}/{Total})", sourceFilePath, percent, processed, total);
                nextLog += 10;
            }
        }

        logger.LogInformation("Built index for {File} with {Count} fragments", sourceFilePath, total);
    }

    private async Task<string> GetBaseUrlAsync(Guid serverId)
    {
        var server = await LlmServerConfigHelper.GetServerConfigAsync(llmServerConfigService, userSettings, serverId);
        var candidate = server?.BaseUrl;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = LlmServerConfig.DefaultOllamaUrl;
        return candidate.Trim();
    }

    private async Task<ServerModel> GetModelAsync()
    {
        var settings = await userSettings.GetSettingsAsync();
        return ModelSelectionHelper.GetEffectiveEmbeddingModel(
            settings.Embedding.Model,
            settings.DefaultModel,
            "RAG vector indexing",
            logger);
    }

    private static string CollectionName(Guid agentId) => $"agent_{agentId:N}";
    private static string Key(string file, int index) => $"{file}#{index:D5}";
}
