using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ChatClient.Api.Services;

/// <summary>
/// Service that handles sampling requests from MCP servers.
/// Sampling allows MCP servers to request the client to perform LLM inference.
/// </summary>
public class McpSamplingService(
    KernelService kernelService,
    ILogger<McpSamplingService> logger)
{
    /// <summary>
    /// Handles a sampling request from an MCP server.
    /// </summary>
    /// <param name="request">The sampling request containing messages and model parameters</param>
    /// <param name="progress">Progress reporting for long-running operations</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The LLM response</returns>
    public async ValueTask<CreateMessageResult> HandleSamplingRequestAsync(
        CreateMessageRequestParams request,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Processing sampling request with {MessageCount} messages",
                request.Messages?.Count ?? 0);

            if (request.Messages == null || request.Messages.Count == 0)
            {
                throw new ArgumentException("Sampling request must contain at least one message");
            }

            // Report progress
            progress?.Report(new ProgressNotificationValue
            {
                Progress = 0,
                Total = 100
            });            // Use the specified model or fall back to a default
            var model = request.ModelPreferences?.Hints?.FirstOrDefault()?.Name ?? "llama3.2";

            // Create a basic kernel without MCP tools to avoid circular dependencies
            var kernel = kernelService.CreateBasicKernel(model);

            // Report progress
            progress?.Report(new ProgressNotificationValue
            {
                Progress = 25,
                Total = 100
            });

            // Convert MCP messages to chat history
            var chatHistory = ConvertMcpMessagesToChatHistory(request.Messages);

            // Report progress
            progress?.Report(new ProgressNotificationValue
            {
                Progress = 50,
                Total = 100
            });

            // Execute the LLM request
            var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
            var response = await chatCompletionService.GetChatMessageContentAsync(
                chatHistory,
                kernel: kernel,
                cancellationToken: cancellationToken);

            // Report progress
            progress?.Report(new ProgressNotificationValue
            {
                Progress = 90,
                Total = 100
            });

            var responseText = response.Content ?? string.Empty;

            logger.LogInformation("Sampling request completed successfully, response length: {Length}",
                responseText.Length);

            // Report completion
            progress?.Report(new ProgressNotificationValue
            {
                Progress = 100,
                Total = 100
            }); return new CreateMessageResult
            {
                Content = new Content
                {
                    Type = "text",
                    Text = responseText
                },
                Model = model,
                StopReason = "end_turn",
                Role = Role.Assistant // The LLM response is always from the assistant
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process sampling request: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Converts MCP protocol messages to a format suitable for the LLM.
    /// </summary>
    private static ChatHistory ConvertMcpMessagesToChatHistory(IEnumerable<SamplingMessage> mcpMessages)
    {
        var chatHistory = new ChatHistory();

        foreach (var mcpMessage in mcpMessages)
        {
            var role = mcpMessage.Role switch
            {
                Role.User => AuthorRole.User,
                Role.Assistant => AuthorRole.Assistant,
                _ => AuthorRole.User // Default to user if unknown role
            };

            // Handle different content types
            string content;
            if (mcpMessage.Content is Content contentObj)
            {
                content = contentObj.Text ?? string.Empty;
            }
            else if (mcpMessage.Content is IList<Content> contentItems)
            {
                // Join multiple content items (text and other types)
                content = string.Join(" ", contentItems.Where(c => c.Text != null).Select(c => c.Text));
            }
            else
            {
                content = mcpMessage.Content?.ToString() ?? string.Empty;
            }

            chatHistory.Add(new ChatMessageContent(role, content));
        }

        return chatHistory;
    }
}
