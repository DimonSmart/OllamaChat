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
        var baseUrl = LlmServerConfigHelper.GetNormalizedOpenAiBaseUrl(server, LlmServerConfig.DefaultOpenAiUrl);
        baseUrl = baseUrl.TrimEnd('/');
        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/chat/completions"
            : $"{baseUrl}/v1/chat/completions";
    }
}
