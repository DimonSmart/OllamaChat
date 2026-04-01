namespace ChatClient.Api.Client.Services.Agentic;

internal static class WorkflowStreamingTextDelta
{
    public static bool IsDuplicateOfCurrentMessage(string? currentContent, string? incomingContent)
    {
        if (string.IsNullOrEmpty(currentContent) || string.IsNullOrEmpty(incomingContent))
        {
            return false;
        }

        if (string.Equals(currentContent, incomingContent, StringComparison.Ordinal))
        {
            return true;
        }

        if (currentContent.Contains(incomingContent, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
