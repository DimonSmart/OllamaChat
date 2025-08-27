using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ChatClient.Tests;

public class ServerConnectionTestServiceTests
{
    [Fact]
    public async Task TestOpenAIConnectionAsync_ShouldReturnFailure_WhenApiKeyIsEmpty()
    {
        // Arrange
        var logger = new LoggerFactory().CreateLogger<ServerConnectionTestService>();
        var configValues = new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var service = new ServerConnectionTestService(logger, configuration);

        var server = new LlmServerConfig
        {
            Id = Guid.NewGuid(),
            Name = "Test OpenAI",
            ServerType = ServerType.ChatGpt,
            BaseUrl = "",
            ApiKey = "", // Empty API key
            HttpTimeoutSeconds = 30
        };

        // Act
        var result = await service.TestConnectionAsync(server);

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Contains("API Key is required", result.ErrorMessage);
    }

    [Fact]
    public async Task TestOpenAIConnectionAsync_ShouldReturnFailure_WhenApiKeyIsInvalid()
    {
        // Arrange
        var logger = new LoggerFactory().CreateLogger<ServerConnectionTestService>();
        var configValues = new Dictionary<string, string?>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var service = new ServerConnectionTestService(logger, configuration);

        var server = new LlmServerConfig
        {
            Id = Guid.NewGuid(),
            Name = "Test OpenAI",
            ServerType = ServerType.ChatGpt,
            BaseUrl = "",
            ApiKey = "invalid-key",
            HttpTimeoutSeconds = 30
        };

        // Act
        var result = await service.TestConnectionAsync(server);

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task TestOllamaConnectionAsync_ShouldReturnFailure_WhenServerIsNotRunning()
    {
        // Arrange
        var logger = new LoggerFactory().CreateLogger<ServerConnectionTestService>();
        var configValues = new Dictionary<string, string?>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var service = new ServerConnectionTestService(logger, configuration);

        var server = new LlmServerConfig
        {
            Id = Guid.NewGuid(),
            Name = "Test Ollama",
            ServerType = ServerType.Ollama,
            BaseUrl = "http://localhost:99999", // Invalid port
            HttpTimeoutSeconds = 5
        };

        // Act
        var result = await service.TestConnectionAsync(server);

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task TestOpenAIConnectionAsync_ShouldUseConfigurationApiKey_WhenServerApiKeyIsEmpty()
    {
        // Arrange
        var logger = new LoggerFactory().CreateLogger<ServerConnectionTestService>();
        var configValues = new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "test-config-key" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var service = new ServerConnectionTestService(logger, configuration);

        var server = new LlmServerConfig
        {
            Id = Guid.NewGuid(),
            Name = "Test OpenAI",
            ServerType = ServerType.ChatGpt,
            BaseUrl = "",
            ApiKey = "", // Empty API key in server config
            HttpTimeoutSeconds = 10
        };

        // Act
        var result = await service.TestConnectionAsync(server);

        // Assert
        // Должен попробовать использовать ключ из конфигурации
        // Результат будет неудачным из-за неправильного ключа, но ошибка будет другой
        Assert.False(result.IsSuccessful);
        Assert.DoesNotContain("API Key is required", result.ErrorMessage);
    }
}
