using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using OpenAI;

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
}
