using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly IChatCompletionService _chatService;
        private readonly ILogger<ChatController> _logger;

        public ChatController(IChatCompletionService chatService, ILogger<ChatController> logger)
        {
            _chatService = chatService;
            _logger = logger;
        }
        
        
        [HttpPost("message")]
        public async Task<IActionResult> SendMessage([FromBody] AppChatRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var chatHistory = new ChatHistory();
                foreach (var message in request.Messages)
                {
                    if (message.Role == ChatRole.System)
                        chatHistory.AddSystemMessage(message.Content);
                    else if (message.Role == ChatRole.User)
                        chatHistory.AddUserMessage(message.Content);
                    else if (message.Role == ChatRole.Assistant)
                        chatHistory.AddAssistantMessage(message.Content);
                }

                var response = await _chatService.GetChatMessageContentAsync(chatHistory, null, null, cancellationToken);
                return Ok(new AppChatResponse { Message = new Message(response.Content, DateTime.Now, ChatRole.Assistant) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing chat message");
                return StatusCode(500, new { error = ex.Message });
            }
        }
        
        
        [HttpPost("stream")]
        public async Task StreamMessage([FromBody] AppChatRequest request, CancellationToken cancellationToken)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";

            try
            {
                var chatHistory = new ChatHistory();
                foreach (var message in request.Messages)
                {
                    if (message.Role == ChatRole.System)
                        chatHistory.AddSystemMessage(message.Content);
                    else if (message.Role == ChatRole.User)
                        chatHistory.AddUserMessage(message.Content);
                    else if (message.Role == ChatRole.Assistant)
                        chatHistory.AddAssistantMessage(message.Content);
                }

                await foreach (var content in _chatService.GetStreamingChatMessageContentsAsync(chatHistory, null, null, cancellationToken))
                {
                    var data = new { content = content.Content };
                    await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(data)}\n\n");
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing streaming chat message");
                var errorData = new { error = ex.Message };
                await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(errorData)}\n\n");
            }
        }
    }
}
