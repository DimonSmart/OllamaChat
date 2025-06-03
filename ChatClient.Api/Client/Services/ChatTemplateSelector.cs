using ChatClient.Shared.Models;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services;

public class ChatTemplateSelector
{
    public RenderFragment SelectTemplate(IAppChatMessage message)
    {
        if (message == null)
            return EmptyTemplate();

        return message.Role == ChatRole.User
            ? UserTemplate(message)
            : AssistantTemplate(message);
    }

    private static RenderFragment EmptyTemplate() => _ => { };

    private static RenderFragment UserTemplate(IAppChatMessage message) => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "user-message message-container");

        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "class", "message-content");
        builder.AddContent(4, message.Content);
        builder.CloseElement();

        builder.OpenElement(5, "div");
        builder.AddAttribute(6, "class", "message-time");
        builder.AddContent(7, message.MsgDateTime.ToString("g"));
        builder.CloseElement();

        builder.CloseElement();
    };

    private RenderFragment AssistantTemplate(IAppChatMessage message) => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "assistant-message message-container");

        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "class", "message-content");
        builder.AddContent(4, message.Content);
        builder.CloseElement();

        builder.OpenElement(5, "div");
        builder.AddAttribute(6, "class", "message-time");
        builder.AddContent(7, message.MsgDateTime.ToString("g"));
        builder.CloseElement();

        builder.CloseElement();
    };
}