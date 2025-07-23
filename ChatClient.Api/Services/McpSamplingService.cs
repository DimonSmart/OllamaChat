using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using DimonSmart.AiUtils;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using OllamaSharp.Models.Exceptions;

namespace ChatClient.Api.Services;

/// <summary>
/// Service that handles sampling requests from MCP servers.
/// Sampling allows MCP servers to request the client to perform LLM inference.
/// </summary>
public class McpSamplingService(
    IServiceProvider serviceProvider,
    IUserSettingsService userSettingsService,
    IOllamaClientService ollamaService,
    ILogger<McpSamplingService> logger)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
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
        IUserSettingsService userSettingsService,
        McpServerConfig? mcpServerConfig = null)
    {
        string? model = null;
        try
        {
            logger.LogInformation("Processing sampling request with {MessageCount} messages", request.Messages?.Count ?? 0);

            if (request.Messages == null || request.Messages.Count == 0)
            {
                throw new ArgumentException("Sampling request must contain at least one message");
            }

            progress?.Report(new ProgressNotificationValue { Progress = 0, Total = 100 });

            model = await DetermineModelToUseAsync(request.ModelPreferences, mcpServerConfig);
            var settings = await userSettingsService.GetSettingsAsync();
            var kernelService = _serviceProvider.GetRequiredService<KernelService>();
            var kernel = await kernelService.CreateBasicKernelAsync(model, TimeSpan.FromSeconds(settings.McpSamplingTimeoutSeconds));

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

            logger.LogInformation("Sampling request completed successfully, response length: {Length}", responseText.Length);

            progress?.Report(new ProgressNotificationValue { Progress = 100, Total = 100 });

            return new CreateMessageResult
            {
                Content = new Content
                {
                    Type = "text",
                    Text = responseText
                },
                Model = model,
                StopReason = "end_turn",
                Role = Role.Assistant
            };
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            logger.LogWarning(ex, "Model {ModelName} does not support tools/function calling for MCP sampling request from server: {ServerName}",
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
                logger.LogError(ex, "MCP sampling request timed out after {TimeoutSeconds} seconds. " +
                    "Consider increasing the MCP sampling timeout in settings if this happens frequently. " +
                    "Request had {MessageCount} messages for server: {ServerName}",
                    timeoutSeconds,
                    request.Messages?.Count ?? 0,
                    mcpServerConfig?.Name ?? "Unknown");
            }
            else
            {
                logger.LogError(ex, "Failed to process sampling request: {Message}", ex.Message);
            }
            throw;
        }
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
            string content = mcpMessage.Content?.Text ?? string.Empty;

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
    private async Task<string> DetermineModelToUseAsync(ModelPreferences? modelPreferences, McpServerConfig? mcpServerConfig)
    {
        var availableModels = await ollamaService.GetModelsAsync();
        var availableModelNames = availableModels.Select(m => m.Name).ToHashSet();

        var requestedModel = modelPreferences?.Hints?.FirstOrDefault()?.Name;
        if (!string.IsNullOrEmpty(requestedModel) && availableModelNames.Contains(requestedModel))
        {
            logger.LogInformation("Using requested model for MCP sampling: {ModelName}", requestedModel);
            return requestedModel;
        }
        if (!string.IsNullOrEmpty(mcpServerConfig?.SamplingModel))
        {
            if (availableModelNames.Contains(mcpServerConfig.SamplingModel))
            {
                logger.LogInformation("Using MCP server configured model for sampling: {ModelName} (Server: {ServerName})",
                    mcpServerConfig.SamplingModel, mcpServerConfig.Name);
                return mcpServerConfig.SamplingModel;
            }
            else
            {
                logger.LogWarning("MCP server configured model '{ModelName}' not available for server '{ServerName}'",
                    mcpServerConfig.SamplingModel, mcpServerConfig.Name);
            }
        }
        var userSettings = await userSettingsService.GetSettingsAsync();
        if (!string.IsNullOrEmpty(userSettings.DefaultModelName))
        {
            if (availableModelNames.Contains(userSettings.DefaultModelName))
            {
                logger.LogInformation("Using user's default model for MCP sampling: {ModelName}", userSettings.DefaultModelName);
                return userSettings.DefaultModelName;
            }
            else
            {
                logger.LogWarning("User's default model '{ModelName}' not available", userSettings.DefaultModelName);
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
        var userSettings = await userSettingsService.GetSettingsAsync();
        return userSettings.McpSamplingTimeoutSeconds;
    }
}
