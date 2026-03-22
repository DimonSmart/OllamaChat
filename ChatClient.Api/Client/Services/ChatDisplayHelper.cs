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
        string.IsNullOrWhiteSpace(name) ? string.Empty : name;

    public static string GetAssistantAvatarText(
        IEnumerable<AgentDescription> agents,
        string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return string.Empty;

        var configuredShortName = agents
            .FirstOrDefault(agent => string.Equals(agent.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
            ?.ShortName;

        if (!string.IsNullOrWhiteSpace(configuredShortName))
            return configuredShortName.Trim();

        var compactName = new string(agentName.Trim().Where(static c => !char.IsWhiteSpace(c)).ToArray());
        if (compactName.Length == 0)
            return string.Empty;

        return compactName.Length <= 3 ? compactName : compactName[..3];
    }
}
