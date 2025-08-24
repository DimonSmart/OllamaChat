#pragma warning disable SKEXP0050
using System.Text.Json;

using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System.Linq;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Text;

namespace ChatClient.Api.Services;

public sealed class RagVectorIndexService(
    IUserSettingsService userSettings,
    IConfiguration configuration,
    ILogger<RagVectorIndexService> logger) : IRagVectorIndexService
{
    public async Task BuildIndexAsync(Guid agentId, string sourceFilePath, string indexFilePath, IProgress<RagVectorIndexStatus>? progress = null, CancellationToken cancellationToken = default, Guid? serverId = null)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException($"Source file not found: {sourceFilePath}");

        var text = await File.ReadAllTextAsync(sourceFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Source file is empty.");

        var modelId = await GetModelIdAsync();
        var baseUrl = await GetBaseUrlAsync(serverId);

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddLogging();
        builder.AddOllamaEmbeddingGenerator(modelId, new Uri(baseUrl));
        var kernel = builder.Build();

        var generator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var lines = TextChunker.SplitPlainTextLines(text, 256, null);
        var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, 512, 64, string.Empty, null).ToList();
        var total = paragraphs.Count;

        logger.LogInformation("Building index for {File} with {Count} fragments", sourceFilePath, total);

        var fragments = new List<RagVectorFragment>();
        var nextLog = 10;
        for (var i = 0; i < total; i++)
        {
            var paragraph = paragraphs[i];
            var embedding = await generator.GenerateAsync(paragraph, cancellationToken: cancellationToken);
            fragments.Add(new RagVectorFragment
            {
                Id = $"{Path.GetFileName(sourceFilePath)}#{i:D5}",
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
            EmbeddingModel = modelId,
            VectorDimensions = fragments.Count > 0 ? fragments[0].Vector.Length : 0,
            Fragments = fragments
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        Directory.CreateDirectory(Path.GetDirectoryName(indexFilePath)!);
        await File.WriteAllTextAsync(indexFilePath, JsonSerializer.Serialize(index, options), cancellationToken);

        logger.LogInformation("Built index {IndexPath} with {Count} fragments", indexFilePath, fragments.Count);
    }

    private async Task<string> GetBaseUrlAsync(Guid? serverId)
    {
        var settings = await userSettings.GetSettingsAsync();
        LlmServerConfig? server = null;
        if (serverId.HasValue && serverId.Value != Guid.Empty)
            server = settings.Llms.FirstOrDefault(s => s.Id == serverId.Value);
        server ??= settings.Llms.FirstOrDefault(s => s.Id == settings.DefaultLlmId) ?? settings.Llms.FirstOrDefault();
        return server?.BaseUrl ?? configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
    }

    private async Task<string> GetModelIdAsync()
    {
        var settings = await userSettings.GetSettingsAsync();
        if (!string.IsNullOrWhiteSpace(settings.EmbeddingModelName))
            return settings.EmbeddingModelName;
        return configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
    }
}
