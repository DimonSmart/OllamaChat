using ChatClient.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController(
    IChatCompletionService chatService,
    ILogger<ChatController> logger,
    Services.KernelService kernelService) : ControllerBase
{
    private const string ContentTypeEventStream = "text/event-stream";
    private static readonly IDictionary<ChatRole, Action<ChatHistory, string>> RoleHandlers =
        new Dictionary<ChatRole, Action<ChatHistory, string>>
        {
            { ChatRole.System,    (history, content) => history.AddSystemMessage(content) },
            { ChatRole.User,      (history, content) => history.AddUserMessage(content) },
            { ChatRole.Assistant, (history, content) => history.AddAssistantMessage(content) }
        };

    [HttpPost("message")]
    public async Task<IActionResult> SendMessage([FromBody] AppChatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var chatHistory = await BuildChatHistory(request);

            var kernel = kernelService.CreateKernel(request.FunctionNames);

            var executionSettings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };

            var response = await chatService.GetChatMessageContentAsync(
                chatHistory,
                executionSettings,
                kernel,
                cancellationToken: cancellationToken);

            logger.LogInformation("Chat message processed successfully");

            return Ok(new AppChatResponse
            {
                Message = new Message(response.Content ?? string.Empty, DateTime.UtcNow, ChatRole.Assistant)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing chat message");
            return StatusCode(500, new { error = "An error occurred processing your request" });
        }
    }

    [HttpPost("stream")]
    public async Task StreamMessage([FromBody] AppChatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            SetStreamHeaders(Response);
            var chatHistory = await BuildChatHistory(request);

            var kernel = kernelService.CreateKernel(request.FunctionNames);
            var executionSettings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };

            await foreach (var content in chatService.GetStreamingChatMessageContentsAsync(
                chatHistory,
                executionSettings,
                kernel,
                cancellationToken: cancellationToken))
            {
                await WriteEventStreamAsync(new { content = content.Content }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error streaming chat message: {Message}", ex.Message);
            await WriteEventStreamAsync(new { error = "An error occurred processing your request" }, cancellationToken);
        }
    }

    private static Task<ChatHistory> BuildChatHistory(AppChatRequest request)
    {
        var chatHistory = new ChatHistory();

        foreach (var message in request.Messages)
        {
            if (RoleHandlers.TryGetValue(message.Role, out var handler))
            {
                handler(chatHistory, message.Content);
            }
        }

        return Task.FromResult(chatHistory);
    }

    private static void SetStreamHeaders(HttpResponse response)
    {
        response.Headers.ContentType = ContentTypeEventStream;
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
    }

    private async Task WriteEventStreamAsync(object data, CancellationToken cancellationToken)
    {
        var jsonData = System.Text.Json.JsonSerializer.Serialize(data);
        await Response.WriteAsync($"data: {jsonData}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
