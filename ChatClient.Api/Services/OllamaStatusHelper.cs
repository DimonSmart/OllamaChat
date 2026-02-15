using ChatClient.Domain.Models;
using System.Net.Sockets;

namespace ChatClient.Api.Services;

public static class OllamaStatusHelper
{
    private const string InstallUrl = "https://ollama.com/download";

    public static OllamaServerStatus CreateStatusFromException(Exception ex, string? baseUrl = null)
    {
        var endpoint = string.IsNullOrWhiteSpace(baseUrl) ? "configured Ollama server" : baseUrl;

        var userFriendlyMessage = ex switch
        {
            HttpRequestException httpEx when IsConnectionRefused(httpEx) =>
                $"Cannot connect to Ollama at {endpoint}. Start it with 'ollama serve'. Install: {InstallUrl}",
            HttpRequestException =>
                $"Cannot connect to Ollama at {endpoint}. Check the server URL in settings. Install: {InstallUrl}",
            TaskCanceledException =>
                $"Connection to Ollama at {endpoint} timed out. Ensure it is running and reachable. Install: {InstallUrl}",
            _ => $"Connection to Ollama failed: {ex.Message}. Install: {InstallUrl}"
        };

        return new OllamaServerStatus
        {
            IsAvailable = false,
            ErrorMessage = userFriendlyMessage
        };
    }

    private static bool IsConnectionRefused(HttpRequestException ex)
    {
        if (ex.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            return true;

        return ex.InnerException is SocketException socketEx
            && socketEx.SocketErrorCode == SocketError.ConnectionRefused;
    }
}
