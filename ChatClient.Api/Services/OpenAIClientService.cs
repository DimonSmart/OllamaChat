using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using OpenAI;
using System.ClientModel;

namespace ChatClient.Api.Services;

public class OpenAIClientService(
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    IConfiguration configuration,
    ILogger<OpenAIClientService> logger) : IOpenAIClientService
{
    private readonly IUserSettingsService _userSettingsService = userSettingsService;
    private readonly ILlmServerConfigService _llmServerConfigService = llmServerConfigService;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<OpenAIClientService> _logger = logger;

    public async Task<IReadOnlyCollection<string>> GetAvailableModelsAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty)
            throw new ArgumentException("ServerId cannot be empty", nameof(serverId));

        try
        {
            var server = await LlmServerConfigHelper.GetServerConfigAsync(_llmServerConfigService, _userSettingsService, serverId, ServerType.ChatGpt);
            if (server == null)
            {
                throw new InvalidOperationException($"No OpenAI server configuration found for serverId: {serverId}");
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
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error retrieving OpenAI models using SDK for serverId: {ServerId}", serverId);
            throw new InvalidOperationException($"Failed to retrieve models for serverId: {serverId}", ex);
        }
    }

    public async Task<bool> IsAvailableAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty)
            throw new ArgumentException("ServerId cannot be empty", nameof(serverId));

        try
        {
            var models = await GetAvailableModelsAsync(serverId, cancellationToken);
            return models.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("OpenAI service not available: {Message}", ex.Message);
            return false;
        }
    }

    private OpenAIClient CreateOpenAIClient(LlmServerConfig server)
    {
        var apiKey = GetEffectiveApiKey(server);

        var credential = new ApiKeyCredential(apiKey);

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

    private string GetEffectiveApiKey(LlmServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
            return server.ApiKey;

        var configApiKey = _configuration["OpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(configApiKey))
        {
            _logger.LogDebug("Using OpenAI API key from configuration for server {ServerName}", server.Name);
            return configApiKey;
        }

        throw new InvalidOperationException("OpenAI API key is required but not configured");
    }

}
