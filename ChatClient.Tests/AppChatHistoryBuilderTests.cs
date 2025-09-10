using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Application.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp;

namespace ChatClient.Tests;

public class AppChatHistoryBuilderTests
{
    private sealed class ThrowingUserSettingsService : IUserSettingsService
    {
        public Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();

        public Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();
    }

    private sealed class ThrowingOllamaClientService : IOllamaClientService
    {
        public Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid serverId) => throw new InvalidOperationException();
        public Task<OllamaApiClient> GetClientAsync(Guid serverId) => throw new InvalidOperationException();
        public Task<float[]> GenerateEmbeddingAsync(string input, ServerModel model, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public bool EmbeddingsAvailable => true;
    }

    private sealed class ThrowingRagVectorSearchService : IRagVectorSearchService
    {
        public Task<RagSearchResponse> SearchAsync(Guid agentId, ReadOnlyMemory<float> queryVector, int maxResults = 5, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class ThrowingRagFileService : IRagFileService
    {
        public Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid id) => throw new InvalidOperationException();
        public Task<RagFile?> GetFileAsync(Guid id, string fileName) => throw new InvalidOperationException();
        public Task AddOrUpdateFileAsync(Guid id, RagFile file) => throw new InvalidOperationException();
        public Task DeleteFileAsync(Guid id, string fileName) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task BuildChatHistoryAsync_SkipsRagAfterFirstUserMessage()
    {
        var builder = new AppChatHistoryBuilder(
            new ThrowingUserSettingsService(),
            new LoggerFactory().CreateLogger<AppChatHistoryBuilder>(),
            new AppForceLastUserReducer(),
            new ThrowingOllamaClientService(),
            new ThrowingRagVectorSearchService(),
            new ThrowingRagFileService(),
            new ConfigurationBuilder().Build());

        var kernel = Kernel.CreateBuilder().Build();
        var messages = new List<IAppChatMessage>
        {
            new AppChatMessage("first", DateTime.UtcNow, ChatRole.User),
            new AppChatMessage("reply", DateTime.UtcNow, ChatRole.Assistant),
            new AppChatMessage("second", DateTime.UtcNow, ChatRole.User)
        };

        var history = await builder.BuildChatHistoryAsync(messages, kernel, Guid.NewGuid(), CancellationToken.None);

        Assert.DoesNotContain(history, m => m.Role == AuthorRole.Tool);
    }

    [Fact]
    public void BuildBaseHistory_PreservesToolRole()
    {
        var builder = new AppChatHistoryBuilder(
            new ThrowingUserSettingsService(),
            new LoggerFactory().CreateLogger<AppChatHistoryBuilder>(),
            new AppForceLastUserReducer(),
            new ThrowingOllamaClientService(),
            new ThrowingRagVectorSearchService(),
            new ThrowingRagFileService(),
            new ConfigurationBuilder().Build());

        var messages = new List<IAppChatMessage>
        {
            new AppChatMessage("ctx", DateTime.UtcNow, ChatRole.Tool)
        };

        var history = builder.BuildBaseHistory(messages);
        Assert.Single(history);
        Assert.Equal(AuthorRole.Tool, history.First().Role);
    }
}
