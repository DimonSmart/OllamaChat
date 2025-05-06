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
                FunctionChoiceBehavior = (request.FunctionNames != null && request.FunctionNames.Any())
                    ? FunctionChoiceBehavior.Auto()
                    : FunctionChoiceBehavior.None()
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
            await WriteEventStreamAsync(new { error = $"Error: {ex.Message}" }, cancellationToken);
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
