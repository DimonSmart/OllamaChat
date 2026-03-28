namespace ChatClient.Api.Client.Services.Agentic;

internal static class WorkflowStreamingTextDelta
{
    public static string GetAppendText(string? currentContent, string? incomingContent)
    {
        if (string.IsNullOrEmpty(incomingContent))
        {
            return string.Empty;
        }

        var current = currentContent ?? string.Empty;
        if (current.Length == 0)
        {
            return incomingContent;
        }

        if (string.Equals(current, incomingContent, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (incomingContent.StartsWith(current, StringComparison.Ordinal))
        {
            return incomingContent[current.Length..];
        }

        if (current.Contains(incomingContent, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var maxOverlap = Math.Min(current.Length, incomingContent.Length);
        for (var overlapLength = maxOverlap; overlapLength > 0; overlapLength--)
        {
            if (current.EndsWith(incomingContent[..overlapLength], StringComparison.Ordinal))
            {
                return incomingContent[overlapLength..];
            }
        }

        return incomingContent;
    }
}
