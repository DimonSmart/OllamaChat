using Microsoft.Extensions.AI;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

internal static class GroupChatAssistantTurnCounter
{
    public static int CountCompletedParticipantTurns(IReadOnlyList<ChatMessage> history) =>
        history.Count(static message =>
            message.Role == ChatRole.Assistant &&
            !message.Contents.Any(static content =>
                content is FunctionCallContent or FunctionResultContent));
}
