using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using ChatClient.Api.Client.Services;
using ChatClient.Shared.Constants;
using System.Linq;

using DimonSmart.AiUtils;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using OllamaSharp.Models.Exceptions;

namespace ChatClient.Api.Services;

/// <summary>
/// Service that handles sampling requests from MCP servers.
/// Sampling allows MCP servers to request the client to perform LLM inference.
/// </summary>
public class McpSamplingService(
    IOllamaClientService ollamaService,
    IUserSettingsService userSettingsService,
    AppForceLastUserReducer reducer,
    ILogger<HttpLoggingHandler> httpLogger,
    ILogger<McpSamplingService> logger)
{
    private readonly IOllamaClientService _ollamaService = ollamaService;
    private readonly IUserSettingsService _userSettingsService = userSettingsService;
    private readonly AppForceLastUserReducer _reducer = reducer;
    private readonly ILogger<HttpLoggingHandler> _httpLogger = httpLogger;
    private readonly ILogger<McpSamplingService> _logger = logger;
    /// <summary>
    /// Handles a sampling request from an MCP server.
    /// </summary>
    /// <param name="request">The sampling request containing messages and model parameters</param>
    /// <param name="progress">Progress reporting for long-running operations</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="mcpServerConfig">Configuration of the MCP server making the request (optional)</param>
    /// <returns>The LLM response</returns>
    public async ValueTask<CreateMessageResult> HandleSamplingRequestAsync(
        CreateMessageRequestParams request,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken,
        McpServerConfig? mcpServerConfig = null,
        Guid serverId = default)
    {
        string? model = null;
        try
        {
            _logger.LogInformation("Processing sampling request with {MessageCount} messages", request.Messages?.Count ?? 0);

            if (request.Messages == null || request.Messages.Count == 0)
            {
                throw new ArgumentException("Sampling request must contain at least one message");
            }

            progress?.Report(new ProgressNotificationValue { Progress = 0, Total = 100 });

            model = await DetermineModelToUseAsync(request.ModelPreferences, mcpServerConfig, serverId);
            var settings = await _userSettingsService.GetSettingsAsync();
            var kernel = await CreateKernelAsync(model, TimeSpan.FromSeconds(settings.McpSamplingTimeoutSeconds), serverId);

            progress?.Report(new ProgressNotificationValue { Progress = 25, Total = 100 });

            var chatHistory = ConvertMcpMessagesToChatHistory(request.Messages);

            progress?.Report(new ProgressNotificationValue { Progress = 50, Total = 100 });
            var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
            var response = await chatCompletionService.GetChatMessageContentAsync(
                chatHistory: chatHistory,
                kernel: kernel,
                cancellationToken: cancellationToken);

            progress?.Report(new ProgressNotificationValue { Progress = 90, Total = 100 });
            var responseText = ThinkTagParser.ExtractThinkAnswer(response.Content ?? string.Empty).Answer;

            _logger.LogInformation("Sampling request completed successfully, response length: {Length}", responseText.Length);

            progress?.Report(new ProgressNotificationValue { Progress = 100, Total = 100 });

            return new CreateMessageResult
            {
                Content = new TextContentBlock
                {
                    Text = responseText
                },
                Model = model!,
                StopReason = "endTurn",
                Role = Role.Assistant
            };
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            _logger.LogWarning(ex, "Model {ModelName} does not support tools/function calling for MCP sampling request from server: {ServerName}",
                model, mcpServerConfig?.Name ?? "Unknown");
            throw new InvalidOperationException(
                $"The model '{model}' does not support function calling/tools. " +
                "MCP sampling requires a model that supports tool use. " +
                "Please configure a different model in the MCP server settings or user settings.", ex);
        }
        catch (Exception ex)
        {
            if (ex is TaskCanceledException or TimeoutException)
            {
                var timeoutSeconds = await GetMcpSamplingTimeoutAsync();
                _logger.LogError(ex, "MCP sampling request timed out after {TimeoutSeconds} seconds. " +
                    "Consider increasing the MCP sampling timeout in settings if this happens frequently. " +
                    "Request had {MessageCount} messages for server: {ServerName}",
                    timeoutSeconds,
                    request.Messages?.Count ?? 0,
                    mcpServerConfig?.Name ?? "Unknown");
            }
            else
            {
                _logger.LogError(ex, "Failed to process sampling request: {Message}", ex.Message);
            }
            throw;
        }
    }

    private async Task<Kernel> CreateKernelAsync(string modelId, TimeSpan timeout, Guid serverId)
    {
        var settings = await _userSettingsService.GetSettingsAsync();
        LlmServerConfig? server = null;
        if (serverId != Guid.Empty)
            server = settings.Llms.FirstOrDefault(s => s.Id == serverId);
        server ??= settings.Llms.FirstOrDefault(s => s.Id == settings.DefaultLlmId) ?? settings.Llms.FirstOrDefault();

        var handler = new HttpClientHandler();
        if (server?.IgnoreSslErrors == true)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var loggingHandler = new HttpLoggingHandler(_httpLogger) { InnerHandler = handler };
        var httpClient = new HttpClient(loggingHandler)
        {
            Timeout = TimeSpan.FromSeconds(server?.HttpTimeoutSeconds ?? (int)timeout.TotalSeconds),
            BaseAddress = new Uri(server?.BaseUrl ?? OllamaDefaults.ServerUrl)
        };

        var password = server?.Password;
        if (!string.IsNullOrWhiteSpace(password))
        {
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{password}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
        }

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(httpClient);
        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));
        builder.Services.AddSingleton<IChatCompletionService>(_ =>
            new AppForceLastUserChatCompletionService(
                new OllamaChatCompletionService(modelId, httpClient: httpClient),
                _reducer));

        return builder.Build();
    }

    /// <summary>
    /// Converts MCP protocol messages to a format suitable for the LLM.
    /// </summary>
    private static ChatHistory ConvertMcpMessagesToChatHistory(IEnumerable<SamplingMessage> samplingMessage)
    {
        var chatHistory = new ChatHistory();

        foreach (var mcpMessage in samplingMessage)
        {
            var role = mcpMessage.Role switch
            {
                Role.User => AuthorRole.User,
                Role.Assistant => AuthorRole.Assistant,
                _ => AuthorRole.User // Default to user if unknown role
            };

            string content = mcpMessage.Content switch
            {
                TextContentBlock tb => tb.Text,
                _ => string.Empty
            };

            chatHistory.Add(new ChatMessageContent(role, content));
        }
        return chatHistory;
    }

    /// <summary>
    /// Determines which model to use for sampling with simplified logic:
    /// 1. If MCP server requests a model that doesn't exist - use MCP server configured model
    /// 2. If not configured in MCP - use user's default model  
    /// 3. If user default not set - return error
    /// No hardcoded fallback
    /// </summary>
    private async Task<string> DetermineModelToUseAsync(
        ModelPreferences? modelPreferences,
        McpServerConfig? mcpServerConfig,
        Guid serverId)
    {
        var availableModels = await _ollamaService.GetModelsAsync(serverId);
        var availableModelNames = availableModels.Select(m => m.Name).ToHashSet();

        var requestedModel = modelPreferences?.Hints?.FirstOrDefault()?.Name;
        if (!string.IsNullOrEmpty(requestedModel) && availableModelNames.Contains(requestedModel))
        {
            _logger.LogInformation("Using requested model for MCP sampling: {ModelName}", requestedModel);
            return requestedModel;
        }
        if (!string.IsNullOrEmpty(mcpServerConfig?.SamplingModel))
        {
            if (availableModelNames.Contains(mcpServerConfig.SamplingModel))
            {
                _logger.LogInformation("Using MCP server configured model for sampling: {ModelName} (Server: {ServerName})",
                    mcpServerConfig.SamplingModel, mcpServerConfig.Name);
                return mcpServerConfig.SamplingModel;
            }
            else
            {
                _logger.LogWarning("MCP server configured model '{ModelName}' not available for server '{ServerName}'",
                    mcpServerConfig.SamplingModel, mcpServerConfig.Name);
            }
        }
        var userSettings = await _userSettingsService.GetSettingsAsync();
        if (!string.IsNullOrEmpty(userSettings.DefaultModelName))
        {
            if (availableModelNames.Contains(userSettings.DefaultModelName))
            {
                _logger.LogInformation("Using user's default model for MCP sampling: {ModelName}", userSettings.DefaultModelName);
                return userSettings.DefaultModelName;
            }
            else
            {
                _logger.LogWarning("User's default model '{ModelName}' not available", userSettings.DefaultModelName);
            }
        }
        var errorMessage = "No valid model available for sampling. ";
        if (!string.IsNullOrEmpty(requestedModel))
        {
            errorMessage += $"Requested model '{requestedModel}' not found. ";
        }
        if (!string.IsNullOrEmpty(mcpServerConfig?.SamplingModel))
        {
            errorMessage += $"MCP server model '{mcpServerConfig.SamplingModel}' not found. ";
        }
        if (!string.IsNullOrEmpty(userSettings.DefaultModelName))
        {
            errorMessage += $"User default model '{userSettings.DefaultModelName}' not found. ";
        }
        errorMessage += "Please configure a valid model in MCP server settings or user settings.";

        throw new InvalidOperationException(errorMessage);
    }

    private async Task<int> GetMcpSamplingTimeoutAsync()
    {
        var userSettings = await _userSettingsService.GetSettingsAsync();
        return userSettings.McpSamplingTimeoutSeconds;
    }
}
