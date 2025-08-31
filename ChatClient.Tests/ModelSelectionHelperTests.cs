using ChatClient.Shared.Helpers;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ChatClient.Tests;

public class ModelSelectionHelperTests
{
    [Fact]
    public void GetEffectiveModel_ConfiguredModelValid_ReturnsConfiguredModel()
    {
        // Arrange
        var configuredModel = new ServerModel(Guid.NewGuid(), "configured-model");
        var uiSelectedModel = new ServerModel(Guid.NewGuid(), "ui-model");
        
        // Act
        var result = ModelSelectionHelper.GetEffectiveModel(
            configuredModel, 
            uiSelectedModel, 
            "Test context");
            
        // Assert
        Assert.Equal(configuredModel.ServerId, result.ServerId);
        Assert.Equal(configuredModel.ModelName, result.ModelName);
    }
    
    [Fact]
    public void GetEffectiveModel_ConfiguredModelInvalid_ReturnsUIModel()
    {
        // Arrange
        var configuredModel = new ServerModel(Guid.Empty, string.Empty); // Invalid
        var uiSelectedModel = new ServerModel(Guid.NewGuid(), "ui-model");
        
        // Act
        var result = ModelSelectionHelper.GetEffectiveModel(
            configuredModel, 
            uiSelectedModel, 
            "Test context");
            
        // Assert
        Assert.Equal(uiSelectedModel.ServerId, result.ServerId);
        Assert.Equal(uiSelectedModel.ModelName, result.ModelName);
    }
    
    [Fact]
    public void GetEffectiveModel_ConfiguredModelNull_ReturnsUIModel()
    {
        // Arrange
        ServerModel? configuredModel = null;
        var uiSelectedModel = new ServerModel(Guid.NewGuid(), "ui-model");
        
        // Act
        var result = ModelSelectionHelper.GetEffectiveModel(
            configuredModel, 
            uiSelectedModel, 
            "Test context");
            
        // Assert
        Assert.Equal(uiSelectedModel.ServerId, result.ServerId);
        Assert.Equal(uiSelectedModel.ModelName, result.ModelName);
    }
    
    [Fact]
    public void GetEffectiveEmbeddingModel_EmbeddingModelValid_ReturnsEmbeddingModel()
    {
        // Arrange
        var embeddingModel = new ServerModel(Guid.NewGuid(), "embedding-model");
        var defaultModel = new ServerModel(Guid.NewGuid(), "default-model");
        
        // Act
        var result = ModelSelectionHelper.GetEffectiveEmbeddingModel(
            embeddingModel, 
            defaultModel, 
            "Test embedding");
            
        // Assert
        Assert.Equal(embeddingModel.ServerId, result.ServerId);
        Assert.Equal(embeddingModel.ModelName, result.ModelName);
    }
    
    [Fact]
    public void GetEffectiveEmbeddingModel_EmbeddingModelInvalid_ReturnsDefaultModel()
    {
        // Arrange
        var embeddingModel = new ServerModel(Guid.Empty, string.Empty); // Invalid
        var defaultModel = new ServerModel(Guid.NewGuid(), "default-model");
        
        // Act
        var result = ModelSelectionHelper.GetEffectiveEmbeddingModel(
            embeddingModel, 
            defaultModel, 
            "Test embedding");
            
        // Assert
        Assert.Equal(defaultModel.ServerId, result.ServerId);
        Assert.Equal(defaultModel.ModelName, result.ModelName);
    }
    
    [Fact]
    public void GetEffectiveAgentModel_AgentModelConfigured_ReturnsAgentModel()
    {
        // Arrange
        var agent = new AgentDescription
        {
            AgentName = "Test Agent",
            LlmId = Guid.NewGuid(),
            ModelName = "agent-model"
        };
        var uiSelectedModel = new ServerModel(Guid.NewGuid(), "ui-model");
        
        // Act
        var configuredModel = agent.LlmId.HasValue && agent.LlmId != Guid.Empty && !string.IsNullOrWhiteSpace(agent.ModelName)
            ? new ServerModel(agent.LlmId.Value, agent.ModelName)
            : null;
        var result = ModelSelectionHelper.GetEffectiveModel(configuredModel, uiSelectedModel, $"Agent: {agent.AgentName}");
            
        // Assert
        Assert.Equal(agent.LlmId.Value, result.ServerId);
        Assert.Equal(agent.ModelName, result.ModelName);
    }
    
    [Fact]
    public void GetEffectiveAgentModel_AgentModelNotConfigured_ReturnsUIModel()
    {
        // Arrange
        var agent = new AgentDescription
        {
            AgentName = "Test Agent",
            LlmId = null,
            ModelName = null
        };
        var uiSelectedModel = new ServerModel(Guid.NewGuid(), "ui-model");
        
        // Act
        var configuredModel = agent.LlmId.HasValue && agent.LlmId != Guid.Empty && !string.IsNullOrWhiteSpace(agent.ModelName)
            ? new ServerModel(agent.LlmId.Value, agent.ModelName)
            : null;
        var result = ModelSelectionHelper.GetEffectiveModel(configuredModel, uiSelectedModel, $"Agent: {agent.AgentName}");
            
        // Assert
        Assert.Equal(uiSelectedModel.ServerId, result.ServerId);
        Assert.Equal(uiSelectedModel.ModelName, result.ModelName);
    }
}
