using ChatClient.Shared.Helpers;
using ChatClient.Shared.Models;
using Xunit;

namespace ChatClient.Tests;

public class ModelSelectionHelperTests
{
    [Fact]
    public void TryGetEffectiveModel_UsesConfiguredWhenUiEmpty()
    {
        var configured = new ServerModelSelection(Guid.NewGuid(), "configured");
        var ui = new ServerModelSelection(null, null);

        var success = ModelSelectionHelper.TryGetEffectiveModel(configured, ui, out var result);

        Assert.True(success);
        Assert.Equal(configured.ServerId, result.ServerId);
        Assert.Equal(configured.ModelName, result.ModelName);
    }

    [Fact]
    public void TryGetEffectiveModel_CombineServerFromUiAndModelFromConfig()
    {
        var configured = new ServerModelSelection(Guid.NewGuid(), "configured");
        var ui = new ServerModelSelection(Guid.NewGuid(), null);

        var success = ModelSelectionHelper.TryGetEffectiveModel(configured, ui, out var result);

        Assert.True(success);
        Assert.Equal(ui.ServerId, result.ServerId);
        Assert.Equal(configured.ModelName, result.ModelName);
    }

    [Fact]
    public void GetEffectiveModel_IncompleteCombination_Throws()
    {
        var configured = new ServerModelSelection(null, "model-only");
        var ui = new ServerModelSelection(null, null);

        Assert.Throws<InvalidOperationException>(() =>
            ModelSelectionHelper.GetEffectiveModel(configured, ui, "test"));
    }

    [Fact]
    public void GetEffectiveEmbeddingModel_EmbeddingModelValid_ReturnsEmbeddingModel()
    {
        var embeddingModel = new ServerModelSelection(Guid.NewGuid(), "embedding-model");
        var defaultModel = new ServerModelSelection(Guid.NewGuid(), "default-model");

        var result = ModelSelectionHelper.GetEffectiveEmbeddingModel(
            embeddingModel,
            defaultModel,
            "embedding");

        Assert.Equal(embeddingModel.ServerId, result.ServerId);
        Assert.Equal(embeddingModel.ModelName, result.ModelName);
    }

    [Fact]
    public void GetEffectiveEmbeddingModel_EmbeddingModelInvalid_ReturnsDefaultModel()
    {
        var embeddingModel = new ServerModelSelection(Guid.Empty, string.Empty);
        var defaultModel = new ServerModelSelection(Guid.NewGuid(), "default-model");

        var result = ModelSelectionHelper.GetEffectiveEmbeddingModel(
            embeddingModel,
            defaultModel,
            "embedding");

        Assert.Equal(defaultModel.ServerId, result.ServerId);
        Assert.Equal(defaultModel.ModelName, result.ModelName);
    }
}

