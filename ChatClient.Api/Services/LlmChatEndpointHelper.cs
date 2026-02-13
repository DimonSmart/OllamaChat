using System.Net.Http.Headers;
using System.Text;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public static class LlmChatEndpointHelper
{
    public static HttpClient CreateHttpClient(LlmServerConfig server, string defaultBaseUrl)
    {
        var client = LlmServerConfigHelper.CreateHttpClient(server, defaultBaseUrl);
        if (!string.IsNullOrWhiteSpace(server.Password))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{server.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        return client;
    }

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
