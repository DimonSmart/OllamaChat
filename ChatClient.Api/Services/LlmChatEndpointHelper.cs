using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public static class LlmChatEndpointHelper
{
    public static string BuildOllamaChatEndpoint(LlmServerConfig server)
    {
        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl)
            ? LlmServerConfig.DefaultOllamaUrl
            : server.BaseUrl;
        return $"{baseUrl.TrimEnd('/')}/api/chat";
    }

    public static string BuildOpenAiChatEndpoint(LlmServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            return "https://api.openai.com/v1/chat/completions";
        }

        var baseUrl = server.BaseUrl.TrimEnd('/');
        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/chat/completions"
            : $"{baseUrl}/v1/chat/completions";
    }
}
