using ChatClient.Domain.Models;
using MudBlazor;

namespace ChatClient.Api.Client.Services;

public static class ChatDisplayHelper
{
    public static string GetFileIcon(string contentType)
    {
        var normalized = contentType?.ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            var ct when ct.StartsWith("image/") => Icons.Material.Filled.Image,
            var ct when ct.Contains("pdf", StringComparison.Ordinal) => Icons.Material.Filled.PictureAsPdf,
            var ct when ct.Contains("text", StringComparison.Ordinal) => Icons.Material.Filled.Description,
            var ct when ct.Contains("word", StringComparison.Ordinal) || ct.Contains("document", StringComparison.Ordinal)
                => Icons.Material.Filled.Description,
            _ => Icons.Material.Filled.AttachFile
        };
    }

    public static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var counter = 0;
        decimal number = bytes;

        while (counter < suffixes.Length - 1 && Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }

        return $"{number:n1} {suffixes[counter]}";
    }

    public static bool IsImageFile(string contentType) =>
        (contentType ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static string GetImageDataUrl(AppChatMessageFile file) =>
        $"data:{file.ContentType};base64,{Convert.ToBase64String(file.Data)}";

    public static string GetAvatarText(string? name) =>
        BuildAvatarText(name, 2);

    public static string? GetAssistantDisplayName(
        IEnumerable<AgentDescription> agents,
        string? agentId)
    {
        var matchingAgent = ResolveAgent(agents, agentId);
        return matchingAgent?.AgentName;
    }

    public static string GetAssistantAvatarText(
        IEnumerable<AgentDescription> agents,
        string? agentId)
    {
        var matchingAgent = ResolveAgent(agents, agentId);
        if (matchingAgent is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(matchingAgent?.AvatarText))
            return BuildAvatarText(matchingAgent.AvatarText, 3);

        if (!string.IsNullOrWhiteSpace(matchingAgent?.ShortName))
            return BuildAvatarText(matchingAgent.ShortName, 3);

        return BuildAvatarText(matchingAgent.AgentName, 3);
    }

    private static AgentDescription? ResolveAgent(
        IEnumerable<AgentDescription> agents,
        string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        return agents.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildAvatarText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var parts = text
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 2)
        {
            var initials = string.Concat(parts
                .Where(static part => part.Length > 0)
                .Take(maxLength)
                .Select(static part => char.ToUpperInvariant(part[0])));
            if (initials.Length > 0)
            {
                return initials;
            }
        }

        var compactText = new string(text
            .Trim()
            .Where(static c => char.IsLetterOrDigit(c))
            .ToArray());
        if (compactText.Length == 0)
            return string.Empty;

        return compactText.Length <= maxLength
            ? compactText.ToUpperInvariant()
            : compactText[..maxLength].ToUpperInvariant();
    }
}
