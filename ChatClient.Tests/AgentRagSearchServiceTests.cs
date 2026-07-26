using ChatClient.Api.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public sealed class AgentRagSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_GeneratesConfiguredEmbeddingAndSearchesAgentIndex()
    {
        var agentId = Guid.NewGuid();
        var embeddingServerId = Guid.NewGuid();
        var settings = new Mock<IUserSettingsService>(MockBehavior.Strict);
        settings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings
            {
                DefaultModel = new ServerModelSelection(Guid.NewGuid(), "chat-model"),
                Embedding = new EmbeddingSettings
                {
                    Model = new ServerModelSelection(embeddingServerId, "embedding-model")
                }
            });
        var ollama = new Mock<IOllamaClientService>(MockBehavior.Strict);
        ollama.SetupGet(service => service.EmbeddingsAvailable).Returns(true);
        ollama.Setup(service => service.GenerateEmbeddingAsync(
                "focused query",
                new ServerModel(embeddingServerId, "embedding-model"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([0.25f, 0.75f]);
        var vectors = new Mock<IRagVectorSearchService>(MockBehavior.Strict);
        var expected = new RagSearchResponse
        {
            Total = 1,
            Results = [new RagSearchResult { FileName = "knowledge.md", Content = "ALPHA-47", Score = 0.9 }]
        };
        ReadOnlyMemory<float> receivedVector = default;
        vectors.Setup(service => service.SearchAsync(
                agentId,
                It.IsAny<ReadOnlyMemory<float>>(),
                5,
                It.IsAny<CancellationToken>()))
            .Callback<Guid, ReadOnlyMemory<float>, int, CancellationToken>((_, vector, _, _) => receivedVector = vector)
            .ReturnsAsync(expected);
        var files = new Mock<IRagFileService>(MockBehavior.Strict);
        files.Setup(service => service.GetFilesAsync(agentId))
            .ReturnsAsync([new RagFile { HasIndex = true }]);

        var service = new AgentRagSearchService(
            settings.Object,
            NullLogger<AgentRagSearchService>.Instance,
            ollama.Object,
            vectors.Object,
            files.Object);

        var result = await service.SearchAsync(agentId, "  focused query  ");

        Assert.Same(expected, result);
        Assert.Equal([0.25f, 0.75f], receivedVector.ToArray());
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryReturnsEmptyResponseWithoutEmbedding()
    {
        var service = new AgentRagSearchService(
            Mock.Of<IUserSettingsService>(MockBehavior.Strict),
            NullLogger<AgentRagSearchService>.Instance,
            Mock.Of<IOllamaClientService>(MockBehavior.Strict),
            Mock.Of<IRagVectorSearchService>(MockBehavior.Strict),
            Mock.Of<IRagFileService>(MockBehavior.Strict));

        var result = await service.SearchAsync(Guid.NewGuid(), "  ");

        Assert.Empty(result.Results);
    }
}
