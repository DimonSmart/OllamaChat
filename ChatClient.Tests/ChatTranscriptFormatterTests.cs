using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.ViewModels;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class ChatTranscriptFormatterTests
{
    [Fact]
    public void Format_Html_RendersToolInvocations()
    {
        var message = new AppChatMessageViewModel
        {
            Role = AppChatRole.Assistant,
            AgentName = "Planner",
            HtmlContent = "<p>Done</p>",
            ToolInvocations =
            [
                new ToolInvocationViewState(
                    "call-1", "sum", "sum", "mcp", "srv", null, false,
                    "request payload", "response payload", null,
                    ToolInvocationStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]
        };

        string html = ChatTranscriptFormatter.Format([message], ChatFormat.Html);

        Assert.Contains("<div class=\"function-call\">", html);
        Assert.Contains("srv.sum", html, StringComparison.Ordinal);
        Assert.Contains("request payload", html, StringComparison.Ordinal);
        Assert.Contains("response payload", html, StringComparison.Ordinal);
    }
}
