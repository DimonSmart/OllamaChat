using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public static class OllamaStatusHelper
{
    public static OllamaServerStatus CreateStatusFromException(Exception ex)
    {
        var userFriendlyMessage = ex switch
        {
            HttpRequestException httpEx when httpEx.Message.Contains("actively refused") =>
                "Ollama server is not running. Please start Ollama using 'ollama serve'",
            HttpRequestException httpEx when httpEx.Message.Contains("name or service not known") =>
                "Cannot connect to Ollama server. Please check the server URL in settings",
            TaskCanceledException =>
                "Connection to Ollama server timed out. Please check if Ollama is running and accessible",
            _ => $"Connection failed: {ex.Message}"
        };

        return new OllamaServerStatus
        {
            IsAvailable = false,
            ErrorMessage = userFriendlyMessage
        };
    }
}
