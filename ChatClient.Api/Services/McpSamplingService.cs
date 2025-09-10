using ChatClient.Api.Client.Services;
using ChatClient.Domain.Models;
using ChatClient.Application.Services;
using DimonSmart.AiUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OllamaSharp.Models.Exceptions;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace ChatClient.Api.Services;

/// <summary>
/// Service that handles sampling requests from MCP servers.
/// Sampling allows MCP servers to request the client to perform LLM inference.
/// </summary>
public class McpSamplingService(
    IOllamaClientService ollamaService,
    IOllamaKernelService ollamaKernelService,
    IOpenAIClientService openAIClientService,
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    AppForceLastUserReducer reducer,
    ILogger<HttpLoggingHandler> httpLogger,
    ILogger<McpSamplingService> logger)
{
    private readonly IOllamaClientService _ollamaService = ollamaService;
    private readonly IOllamaKernelService _ollamaKernelService = ollamaKernelService;
    private readonly IOpenAIClientService _openAIClientService = openAIClientService;
    private readonly IUserSettingsService _userSettingsService = userSettingsService;
    private readonly ILlmServerConfigService _llmServerConfigService = llmServerConfigService;
    private readonly AppForceLastUserReducer _reducer = reducer;
    private readonly ILogger<HttpLoggingHandler> _httpLogger = httpLogger;
    private readonly ILogger<McpSamplingService> _logger = logger;

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
        ServerModel? model = null;
        try
        {
            ValidateRequest(request);
            progress?.Report(new ProgressNotificationValue { Progress = 0, Total = 100 });

            model = await DetermineModelToUseAsync(request.ModelPreferences, mcpServerConfig, serverId);
            var kernel = await CreateKernelForSamplingAsync(model);
            progress?.Report(new ProgressNotificationValue { Progress = 25, Total = 100 });

            var response = await ProcessSamplingRequestAsync(request, kernel, progress, cancellationToken);
            progress?.Report(new ProgressNotificationValue { Progress = 100, Total = 100 });

            return CreateSuccessfulResult(response, model.ModelName);
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            LogModelToolSupportError(ex, model?.ModelName, mcpServerConfig);
            throw new InvalidOperationException(
                $"The model '{model?.ModelName}' does not support function calling/tools. " +
                "MCP sampling requires a model that supports tool use. " +
                "Please configure a different model in the MCP server settings or user settings.", ex);
        }
        catch (Exception ex)
        {
            await LogAndHandleExceptionAsync(ex, request, mcpServerConfig);
            throw;
        }
    }

    private void ValidateRequest(CreateMessageRequestParams request)
    {
        _logger.LogInformation("Processing sampling request with {MessageCount} messages", request.Messages?.Count ?? 0);

        if (request.Messages == null || request.Messages.Count == 0)
        {
            throw new ArgumentException("Sampling request must contain at least one message");
        }
    }

    private async Task<Kernel> CreateKernelForSamplingAsync(ServerModel model)
    {
        var settings = await _userSettingsService.GetSettingsAsync();
        return await CreateKernelAsync(model, TimeSpan.FromSeconds(settings.McpSamplingTimeoutSeconds));
    }

    private async Task<ChatMessageContent> ProcessSamplingRequestAsync(
        CreateMessageRequestParams request,
        Kernel kernel,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        var chatHistory = ConvertMcpMessagesToChatHistory(request.Messages);
        progress?.Report(new ProgressNotificationValue { Progress = 50, Total = 100 });

        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
        var response = await chatCompletionService.GetChatMessageContentAsync(
            chatHistory: chatHistory,
            kernel: kernel,
            cancellationToken: cancellationToken);

        progress?.Report(new ProgressNotificationValue { Progress = 90, Total = 100 });
        return response;
    }

    private CreateMessageResult CreateSuccessfulResult(ChatMessageContent response, string model)
    {
        var responseText = ThinkTagParser.ExtractThinkAnswer(response.Content ?? string.Empty).Answer;
        _logger.LogInformation("Sampling request completed successfully, response length: {Length}", responseText.Length);

        return new CreateMessageResult
        {
            Content = new TextContentBlock { Text = responseText },
            Model = model!,
            StopReason = "endTurn",
            Role = Role.Assistant
        };
    }

    private void LogModelToolSupportError(ModelDoesNotSupportToolsException ex, string? model, McpServerConfig? mcpServerConfig)
    {
        _logger.LogWarning(ex, "Model {ModelName} does not support tools/function calling for MCP sampling request from server: {ServerName}",
            model, mcpServerConfig?.Name ?? "Unknown");
    }

    private async Task LogAndHandleExceptionAsync(Exception ex, CreateMessageRequestParams request, McpServerConfig? mcpServerConfig)
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
    }

    private async Task<Kernel> CreateKernelAsync(ServerModel model, TimeSpan timeout)
    {
        var server = await LlmServerConfigHelper.GetServerConfigAsync(_llmServerConfigService, _userSettingsService, model.ServerId)
            ?? throw new InvalidOperationException("No server configuration found for the specified model");

        var builder = Kernel.CreateBuilder();
        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));

        var chatService = server.ServerType == ServerType.ChatGpt
            ? await _openAIClientService.GetClientAsync(model)
            : await _ollamaKernelService.GetClientAsync(model.ServerId);

        builder.Services.AddSingleton<IChatCompletionService>(_ =>
            new AppForceLastUserChatCompletionService(chatService, _reducer));

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
    private async Task<ServerModel> DetermineModelToUseAsync(
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
            return new ServerModel(serverId, requestedModel);
        }
        if (!string.IsNullOrEmpty(mcpServerConfig?.SamplingModel))
        {
            if (availableModelNames.Contains(mcpServerConfig.SamplingModel))
            {
                _logger.LogInformation("Using MCP server configured model for sampling: {ModelName} (Server: {ServerName})",
                    mcpServerConfig.SamplingModel, mcpServerConfig.Name);
                return new ServerModel(serverId, mcpServerConfig.SamplingModel);
            }
            else
            {
                _logger.LogWarning("MCP server configured model '{ModelName}' not available for server '{ServerName}'",
                    mcpServerConfig.SamplingModel, mcpServerConfig.Name);
            }
        }
        var userSettings = await _userSettingsService.GetSettingsAsync();
        var defaultModel = userSettings.DefaultModel;
        if (!string.IsNullOrEmpty(defaultModel.ModelName))
        {
            if (availableModelNames.Contains(defaultModel.ModelName))
            {
                _logger.LogInformation("Using user's default model for MCP sampling: {ModelName}", defaultModel.ModelName);
                return new ServerModel(serverId, defaultModel.ModelName);
            }
            else
            {
                _logger.LogWarning("User's default model '{ModelName}' not available", defaultModel.ModelName);
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
        if (!string.IsNullOrEmpty(defaultModel.ModelName))
        {
            errorMessage += $"User default model '{defaultModel.ModelName}' not found. ";
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
