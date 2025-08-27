using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;
using System.ClientModel;

namespace ChatClient.Api.Services;

public class OpenAIClientService(
    IUserSettingsService userSettingsService,
    ILogger<OpenAIClientService> logger) : IOpenAIClientService
{
    private readonly IUserSettingsService _userSettingsService = userSettingsService;
    private readonly ILogger<OpenAIClientService> _logger = logger;

    public async Task<IChatCompletionService> GetClientAsync(ServerModel serverModel, CancellationToken cancellationToken = default)
    {
        if (serverModel.ServerId == Guid.Empty)
            throw new ArgumentException("ServerId cannot be empty", nameof(serverModel));

        if (string.IsNullOrWhiteSpace(serverModel.ModelName))
            throw new ArgumentException("ModelName cannot be null or empty", nameof(serverModel));

        var server = await LlmServerConfigHelper.GetServerConfigAsync(_userSettingsService, serverModel.ServerId, ServerType.ChatGpt);
        if (server == null)
        {
            throw new InvalidOperationException($"No OpenAI server configuration found for serverId: {serverModel.ServerId}");
        }

        if (string.IsNullOrWhiteSpace(server.ApiKey))
        {
            throw new InvalidOperationException("OpenAI API key is required but not configured");
        }

        var openAIClient = new OpenAIChatCompletionService(
            modelId: serverModel.ModelName,
            apiKey: server.ApiKey,
            httpClient: CreateHttpClient(server));

        return openAIClient;
    }

    public async Task<List<string>> GetAvailableModelsAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty)
            throw new ArgumentException("ServerId cannot be empty", nameof(serverId));

        try
        {
            var server = await LlmServerConfigHelper.GetServerConfigAsync(_userSettingsService, serverId, ServerType.ChatGpt);
            if (server == null)
            {
                throw new InvalidOperationException($"No OpenAI server configuration found for serverId: {serverId}");
            }

            if (string.IsNullOrWhiteSpace(server.ApiKey))
            {
                throw new InvalidOperationException("OpenAI API key is required but not configured");
            }

            var openAIClient = CreateOpenAIClient(server);
            var modelClient = openAIClient.GetOpenAIModelClient();

            _logger.LogDebug("Requesting OpenAI models using OpenAI SDK");

            var modelsResponse = await modelClient.GetModelsAsync(cancellationToken);

            if (modelsResponse?.Value != null)
            {
                var modelIds = modelsResponse.Value.Select(m => m.Id).ToList();
                _logger.LogDebug("Retrieved {Count} models from OpenAI using SDK", modelIds.Count);
                return modelIds;
            }

            _logger.LogWarning("Invalid response from OpenAI models API");
            throw new InvalidOperationException("Failed to retrieve models from OpenAI API");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving OpenAI models using SDK");
            throw new InvalidOperationException($"Failed to retrieve models for serverId: {serverId}", ex);
        }
    }

    public async Task<bool> IsAvailableAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty)
            throw new ArgumentException("ServerId cannot be empty", nameof(serverId));

        try
        {
            var testModel = new ServerModel(serverId, "gpt-3.5-turbo");
            var client = await GetClientAsync(testModel, cancellationToken);
            return client != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI service not available: {Message}", ex.Message);
            return false;
        }
    }

    private OpenAIClient CreateOpenAIClient(LlmServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.ApiKey))
            throw new ArgumentException("OpenAI API key is required", nameof(server));

        var credential = new ApiKeyCredential(server.ApiKey);

        if (!string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(server.BaseUrl)
            };
            return new OpenAIClient(credential, options);
        }

        return new OpenAIClient(credential);
    }

    private HttpClient CreateHttpClient(LlmServerConfig server)
    {
        return LlmServerConfigHelper.CreateHttpClient(server);
    }
}
