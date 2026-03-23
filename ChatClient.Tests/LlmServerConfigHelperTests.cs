using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Configuration;
using OpenAI;
using System.ClientModel.Primitives;
using System.Net.Http;
using System.Reflection;

namespace ChatClient.Tests;

public class LlmServerConfigHelperTests
{
    [Fact]
    public void CreateOpenAIClientOptions_UsesConfiguredTimeoutAndEndpoint()
    {
        var server = new LlmServerConfig
        {
            HttpTimeoutSeconds = 321
        };
        var endpoint = new Uri("http://localhost:11434/v1/");

        OpenAIClientOptions options = LlmServerConfigHelper.CreateOpenAIClientOptions(server, endpoint);

        Assert.Equal(TimeSpan.FromSeconds(321), options.NetworkTimeout);
        Assert.Equal(endpoint, options.Endpoint);
        var transport = Assert.IsType<HttpClientPipelineTransport>(options.Transport);
        var httpClient = GetHttpClient(transport);
        Assert.Equal(TimeSpan.FromSeconds(321), httpClient.Timeout);
        Assert.Equal(endpoint, httpClient.BaseAddress);
    }

    [Fact]
    public void GetRequestTimeout_FallsBackToDefault_WhenConfiguredTimeoutIsNotPositive()
    {
        var server = new LlmServerConfig
        {
            HttpTimeoutSeconds = 0
        };

        var timeout = LlmServerConfigHelper.GetRequestTimeout(server);

        Assert.Equal(TimeSpan.FromSeconds(600), timeout);
    }

    [Fact]
    public void GetConfiguredOpenAiApiKey_UsesNamedSecretBeforeGlobalFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmServers:ApiKeys:azure-eastus2"] = "named-secret",
                ["OpenAI:ApiKey"] = "global-secret"
            })
            .Build();
        var server = new LlmServerConfig
        {
            Id = Guid.NewGuid(),
            ServerType = ServerType.ChatGpt,
            ApiKeySecretName = "azure-eastus2"
        };

        var apiKey = LlmServerConfigHelper.GetConfiguredOpenAiApiKey(configuration, server);

        Assert.Equal("named-secret", apiKey);
    }

    [Fact]
    public void GetConfiguredOpenAiApiKey_UsesInlineApiKeyBeforeSecretStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmServers:ApiKeys:azure-eastus2"] = "named-secret",
                ["OpenAI:ApiKey"] = "global-secret"
            })
            .Build();
        var server = new LlmServerConfig
        {
            Id = Guid.NewGuid(),
            ServerType = ServerType.ChatGpt,
            ApiKey = "inline-secret",
            ApiKeySecretName = "azure-eastus2"
        };

        var apiKey = LlmServerConfigHelper.GetConfiguredOpenAiApiKey(configuration, server);

        Assert.Equal("inline-secret", apiKey);
    }

    [Fact]
    public void GetNormalizedOpenAiBaseUrl_AppendsOpenAiV1_ForAzureRootUrl()
    {
        var server = new LlmServerConfig
        {
            ServerType = ServerType.ChatGpt,
            BaseUrl = "https://example-eastus2.openai.azure.com/"
        };

        var baseUrl = LlmServerConfigHelper.GetNormalizedOpenAiBaseUrl(server, LlmServerConfig.DefaultOpenAiUrl);

        Assert.Equal("https://example-eastus2.openai.azure.com/openai/v1", baseUrl);
    }

    private static HttpClient GetHttpClient(HttpClientPipelineTransport transport)
    {
        var clientField = typeof(HttpClientPipelineTransport)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(static field => typeof(HttpClient).IsAssignableFrom(field.FieldType));

        Assert.NotNull(clientField);
        return Assert.IsType<HttpClient>(clientField!.GetValue(transport));
    }
}
