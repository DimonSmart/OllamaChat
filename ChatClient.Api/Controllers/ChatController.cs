using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatCompletionService _chatService;
    private readonly ISystemPromptService _systemPromptService;
    private readonly ILogger<ChatController> _logger;

    private const string ContentTypeEventStream = "text/event-stream";
    private static readonly IDictionary<ChatRole, Action<ChatHistory, string>> RoleHandlers =
        new Dictionary<ChatRole, Action<ChatHistory, string>>
        {
            { ChatRole.System,    (history, content) => history.AddSystemMessage(content) },
            { ChatRole.User,      (history, content) => history.AddUserMessage(content) },
            { ChatRole.Assistant, (history, content) => history.AddAssistantMessage(content) }
        };

    public ChatController(
        IChatCompletionService chatService,
        ISystemPromptService systemPromptService,
        ILogger<ChatController> logger)
    {
        _chatService = chatService;
        _systemPromptService = systemPromptService;
        _logger = logger;
    }    
    
    [HttpPost("message")]
    public async Task<IActionResult> SendMessage([FromBody] AppChatRequest request, CancellationToken cancellationToken)
    {
        var chatHistory = await BuildChatHistory(request);
        var response = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);

        return Ok(new AppChatResponse
        {
            Message = new Message(response.Content ?? string.Empty, DateTime.UtcNow, ChatRole.Assistant)
        });
    }

    [HttpPost("stream")]
    public async Task StreamMessage([FromBody] AppChatRequest request, CancellationToken cancellationToken)
    {
        SetStreamHeaders();
        var chatHistory = await BuildChatHistory(request);

        await foreach (var content in _chatService.GetStreamingChatMessageContentsAsync(chatHistory, cancellationToken: cancellationToken))
        {
            await WriteEventStreamAsync(new { content = content.Content }, cancellationToken);
        }
    }
    
    private async Task<ChatHistory> BuildChatHistory(AppChatRequest request)
    {
        var chatHistory = new ChatHistory();

        // If a system prompt ID is specified and there's no system message already
        if (!string.IsNullOrEmpty(request.SystemPromptId) && 
            !request.Messages.Any(m => m.Role == ChatRole.System))
        {
            var systemPrompt = await _systemPromptService.GetPromptByIdAsync(request.SystemPromptId);
            if (systemPrompt != null)
            {
                chatHistory.AddSystemMessage(systemPrompt.Content);
            }
        }

        foreach (var message in request.Messages)
        {
            if (RoleHandlers.TryGetValue(message.Role, out var handler))
            {
                handler(chatHistory, message.Content);
            }
        }

        return chatHistory;
    }

    private void SetStreamHeaders()
    {
        Response.Headers["Content-Type"] = ContentTypeEventStream;
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
    }

    private async Task WriteEventStreamAsync(object data, CancellationToken cancellationToken)
    {
        var jsonData = System.Text.Json.JsonSerializer.Serialize(data);
        await Response.WriteAsync($"data: {jsonData}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
