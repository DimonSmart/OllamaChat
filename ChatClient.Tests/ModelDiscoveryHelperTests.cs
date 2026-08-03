using ChatClient.Application.Helpers;
using ChatClient.Domain.Models;
using Xunit;

namespace ChatClient.Tests;

public class ModelDiscoveryHelperTests
{
    [Fact]
    public void FilterByName_ReturnsMatchingModelsIgnoringCase()
    {
        var models = new[]
        {
            new OllamaModel { Name = "Llama-3" },
            new OllamaModel { Name = "Qwen-3" },
            new OllamaModel { Name = "llama-vision" }
        };

        var result = ModelDiscoveryHelper.FilterByName(models, "LLAMA");

        Assert.Equal(["Llama-3", "llama-vision"], result.Select(model => model.Name));
    }

    [Fact]
    public void FilterByName_WithBlankFilter_ReturnsAllModels()
    {
        var models = new[] { new OllamaModel { Name = "Llama-3" } };

        var result = ModelDiscoveryHelper.FilterByName(models, " ");

        Assert.Same(models[0], Assert.Single(result));
    }
}
