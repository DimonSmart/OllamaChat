#pragma warning disable SKEXP0050
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using ChatClient.Shared.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Text;
using System.Linq;
using System.Text.Json;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorIndexService(
    IUserSettingsService userSettings,
    ILlmServerConfigService llmServerConfigService,
    ILogger<RagVectorIndexService> logger) : IRagVectorIndexService
{
    public async Task BuildIndexAsync(Guid agentId, string sourceFilePath, string indexFilePath, IProgress<RagVectorIndexStatus>? progress = null, CancellationToken cancellationToken = default, Guid serverId = default)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException($"Source file not found: {sourceFilePath}");

        var text = await File.ReadAllTextAsync(sourceFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Source file is empty.");

        var model = await GetModelAsync();
        var targetServer = serverId == Guid.Empty ? model.ServerId : serverId;
        var baseUrl = await GetBaseUrlAsync(targetServer);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Base URL cannot be empty for embedding service");
        }

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddLogging();
        builder.AddOllamaEmbeddingGenerator(model.ModelName, new Uri(baseUrl.Trim()));
        var kernel = builder.Build();

        var generator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var settings = await userSettings.GetSettingsAsync();
        var maxTokensPerLine = settings.RagLineChunkSize;
        var maxTokensPerParagraph = settings.RagParagraphChunkSize;
        var paragraphOverlap = settings.RagParagraphOverlap;
        var lines = TextChunker.SplitPlainTextLines(text, maxTokensPerLine, null);
        var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, maxTokensPerParagraph, paragraphOverlap, string.Empty, null).ToList();
        var total = paragraphs.Count;

        logger.LogInformation("Building index for {File} with {Count} fragments", sourceFilePath, total);

        List<RagVectorFragment> fragments = [];
        var nextLog = 10;
        for (var i = 0; i < total; i++)
        {
            var paragraph = paragraphs[i];
            var embedding = await generator.GenerateAsync(paragraph, cancellationToken: cancellationToken);
            fragments.Add(new RagVectorFragment
            {
                Index = i,
                Text = paragraph,
                Vector = embedding.Vector.ToArray()
            });

            var processed = i + 1;
            progress?.Report(new(agentId, Path.GetFileName(sourceFilePath), processed, total));
            var percent = processed * 100 / total;
            if (percent >= nextLog)
            {
                logger.LogInformation("Building index for {File}: {Percent}% ({Processed}/{Total})", sourceFilePath, percent, processed, total);
                nextLog += 10;
            }
        }

        var info = new FileInfo(sourceFilePath);
        var index = new RagVectorIndex
        {
            SourceFileName = Path.GetFileName(sourceFilePath),
            SourceModifiedTime = info.LastWriteTimeUtc,
            EmbeddingModel = model.ModelName,
            VectorDimensions = fragments.Count > 0 ? fragments[0].Vector.Length : 0,
            Fragments = fragments
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        Directory.CreateDirectory(Path.GetDirectoryName(indexFilePath)!);
        await File.WriteAllTextAsync(indexFilePath, JsonSerializer.Serialize(index, options), cancellationToken);

        logger.LogInformation("Built index {IndexPath} with {Count} fragments", indexFilePath, fragments.Count);
    }

    private async Task<string> GetBaseUrlAsync(Guid serverId)
    {
        var server = await LlmServerConfigHelper.GetServerConfigAsync(llmServerConfigService, userSettings, serverId);
        var candidate = server?.BaseUrl;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = LlmServerConfig.DefaultOllamaUrl;
        }
        return candidate.Trim();
    }

    private async Task<ServerModel> GetModelAsync()
    {
        var settings = await userSettings.GetSettingsAsync();
        return ModelSelectionHelper.GetEffectiveEmbeddingModel(
            settings.EmbeddingModel,
            settings.DefaultModel,
            "RAG vector indexing",
            logger);
    }
}
