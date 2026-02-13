using ChatClient.Domain.Models;
using ModelContextProtocol.Client;
using System.Collections.Concurrent;
using System.Net;

namespace ChatClient.Api.Services;

internal sealed class McpFunctionIndexBuilder(
    IMcpClientService clientService,
    IOllamaClientService ollamaService,
    ILogger logger)
{
    public async Task BuildAsync(
        ConcurrentDictionary<string, float[]> index,
        ServerModel model,
        Guid? serverId,
        CancellationToken cancellationToken)
    {
        var clients = await clientService.GetMcpClientsAsync(cancellationToken);
        foreach (var client in clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexClientFunctionsAsync(index, client, model, serverId, cancellationToken);
        }
    }

    private async Task IndexClientFunctionsAsync(
        ConcurrentDictionary<string, float[]> index,
        McpClient client,
        ServerModel model,
        Guid? serverId,
        CancellationToken cancellationToken)
    {
        var tools = await clientService.GetMcpTools(client, cancellationToken);
        foreach (var tool in tools)
        {
            await IndexSingleToolAsync(index, client, tool, model, serverId, cancellationToken);
        }
    }

    private async Task IndexSingleToolAsync(
        ConcurrentDictionary<string, float[]> index,
        McpClient client,
        McpClientTool tool,
        ServerModel model,
        Guid? serverId,
        CancellationToken cancellationToken)
    {
        string text = $"{tool.Name}. {tool.Description}";
        try
        {
            var embeddingModel = new ServerModel(serverId ?? model.ServerId, model.ModelName);
            var embedding = await ollamaService.GenerateEmbeddingAsync(text, embeddingModel, cancellationToken);
            index[$"{client.ServerInfo.Name}:{tool.Name}"] = embedding;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogError("Embedding model '{Model}' not found. Skipping MCP function indexing.", model.ModelName);
            throw;
        }
        catch (Exception ex)
        {
            if (!ollamaService.EmbeddingsAvailable)
            {
                logger.LogError(ex, "Embedding service unavailable. Stopping MCP function indexing.");
                throw;
            }

            logger.LogError(ex, "Failed to index tool {Name}", tool.Name);
        }
    }
}
