using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Application.Services.Agentic;

public static class AppChatRoleExtensions
{
    public static ChatRole ToAiChatRole(this AppChatRole role) =>
        role switch
        {
            AppChatRole.User => ChatRole.User,
            AppChatRole.Assistant => ChatRole.Assistant,
            AppChatRole.System => ChatRole.System,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported app chat role.")
        };

    public static AppChatRole ToAppChatRole(this ChatRole role)
    {
        if (role == ChatRole.User)
        {
            return AppChatRole.User;
        }

        if (role == ChatRole.Assistant)
        {
            return AppChatRole.Assistant;
        }

        if (role == ChatRole.System)
        {
            return AppChatRole.System;
        }

        throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported AI chat role.");
    }
}
