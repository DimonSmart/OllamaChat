using ChatClient.Api.Services.Rag;

namespace ChatClient.Tests;

public sealed class RagVectorDataStoreTests
{
    [Fact]
    public void ToMaxCosineDistance_ConvertsMinimumRelevance()
    {
        var distance = RagVectorDataStore.ToMaxCosineDistance(0.7);

        Assert.Equal(0.3, distance, precision: 10);
    }

    [Fact]
    public void ToRelevanceScore_ConvertsCosineDistance()
    {
        var relevance = RagVectorDataStore.ToRelevanceScore(0.1);

        Assert.Equal(0.9, relevance, precision: 10);
    }
}
