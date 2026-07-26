using ChatClient.Application.Helpers;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class LegacyRagMigrationService(
    IConfiguration configuration,
    IKnowledgeStoreRepository stores,
    IKnowledgeDocumentStorage documents,
    IAgentTemplateService agents,
    IUserSettingsService settings,
    IKnowledgeIndexBackgroundService indexer,
    ILogger<LegacyRagMigrationService> logger)
{
    private const string CompletionMarker = "legacy-agent-rag-v1.completed";
    private const string MigrationDescription = "Migrated from the legacy agent-owned RAG storage.";

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var storageRoot = StoragePathResolver.GetStorageRoot(configuration);
        var markerPath = Path.Combine(storageRoot, "UserData", CompletionMarker);
        if (File.Exists(markerPath))
            return;

        var legacyAgentsRoot = Path.Combine(storageRoot, "UserData", "agents");
        var allSucceeded = true;
        foreach (var agent in await agents.GetAllAsync())
        {
            if (agent.Id == Guid.Empty)
                continue;

            var filesPath = Path.Combine(legacyAgentsRoot, agent.Id.ToString(), "files");
            if (!Directory.Exists(filesPath))
                continue;

            try
            {
                await MigrateAgentAsync(agent, filesPath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                allSucceeded = false;
                logger.LogError(exception, "Legacy RAG migration failed for agent {AgentId}", agent.Id);
            }
        }

        if (!allSucceeded)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await File.WriteAllTextAsync(markerPath, string.Empty, cancellationToken);
        logger.LogInformation("Legacy RAG migration completed. Original legacy data was preserved.");
    }

    private async Task MigrateAgentAsync(AgentTemplateDefinition agent, string filesPath, CancellationToken ct)
    {
        var legacyFiles = Directory.EnumerateFiles(filesPath, "*", SearchOption.TopDirectoryOnly).ToList();
        if (legacyFiles.Count == 0)
            return;

        var store = (await stores.GetAllAsync(ct)).FirstOrDefault(candidate => candidate.Id == agent.Id);
        if (store is null)
        {
            var appSettings = await settings.GetSettingsAsync(ct);
            var embedding = ModelSelectionHelper.GetEffectiveEmbeddingModel(
                appSettings.Embedding.Model,
                appSettings.DefaultModel,
                "legacy RAG migration",
                logger);
            store = new KnowledgeStore
            {
                Id = agent.Id,
                Name = $"{agent.AgentName} - Migrated Knowledge",
                Description = MigrationDescription,
                Configuration = new KnowledgeStoreIndexConfiguration { ServerId = embedding.ServerId, Model = embedding.ModelName }
            };
            await stores.SaveAsync(store, ct);
        }

        var attached = false;
        if (!agent.KnowledgeStoreIds.Contains(store.Id))
        {
            agent.KnowledgeStoreIds.Add(store.Id);
            await agents.UpdateAsync(agent);
            attached = true;
        }

        var imported = false;
        foreach (var path in legacyFiles)
        {
            var fileName = Path.GetFileName(path);
            try
            {
                var content = NormalizeLegacyText(await File.ReadAllTextAsync(path, Encoding.UTF8, ct));
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
                var existing = store.Documents.FirstOrDefault(document => document.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                if (existing?.SourceHash == hash)
                    continue;

                var document = new KnowledgeDocument
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    FileName = fileName,
                    ContentType = "text/plain",
                    SourceHash = hash,
                    IndexedSourceHash = existing?.IndexedSourceHash,
                    Size = Encoding.UTF8.GetByteCount(content),
                    UpdatedUtc = DateTime.UtcNow
                };
                await documents.WriteLegacyTextAsync(store.Id, document.Id, content, content, ct);
                if (existing is null)
                    store.Documents.Add(document);
                else
                    store.Documents[store.Documents.IndexOf(existing)] = document;
                imported = true;
                await stores.SaveAsync(store, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Legacy RAG migration failed for agent {AgentId}, file {FileName}", agent.Id, fileName);
                throw;
            }
        }

        if (imported || attached)
        {
            store.Index.State = store.Documents.Count == 0 ? KnowledgeStoreIndexState.NotIndexed : KnowledgeStoreIndexState.Outdated;
            await stores.SaveAsync(store, ct);
            indexer.RequestRebuild();
        }
    }

    private static string NormalizeLegacyText(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Legacy extracted text is empty.");
        return normalized + "\n";
    }
}
