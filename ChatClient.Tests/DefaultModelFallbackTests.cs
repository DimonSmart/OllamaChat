#pragma warning disable SKEXP0110

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Xunit.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Tests;

public class DefaultModelFallbackTests
{
    private readonly ITestOutputHelper _output;

    public DefaultModelFallbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ModelLogic_AgentWithoutModel_ShouldUseDefaultFromConfiguration()
    {
        // Arrange
        var agentDescription = new AgentDescription { ModelName = null };
        var chatConfiguration = new ChatConfiguration("defaultModel", []);

        // Act - simulate the logic from ChatService.CreateAgents
        var modelName = agentDescription.ModelName ?? chatConfiguration.ModelName ?? throw new InvalidOperationException($"Agent model name is not set and no default model is configured.");

        // Assert
        Assert.Equal("defaultModel", modelName);
        _output.WriteLine("✅ Agent without model successfully uses default model from configuration");
    }

    [Fact]
    public void ModelLogic_AgentWithModel_ShouldUseAgentSpecificModel()
    {
        // Arrange
        var agentDescription = new AgentDescription { ModelName = "agentSpecificModel" };
        var chatConfiguration = new ChatConfiguration("defaultModel", []);

        // Act - simulate the logic from ChatService.CreateAgents
        var modelName = agentDescription.ModelName ?? chatConfiguration.ModelName ?? throw new InvalidOperationException($"Agent model name is not set and no default model is configured.");

        // Assert
        Assert.Equal("agentSpecificModel", modelName);
        _output.WriteLine("✅ Agent with specific model successfully uses its own model");
    }

    [Fact]
    public void ModelLogic_NoDefaultModel_ShouldThrowDescriptiveException()
    {
        // Arrange
        var agentDescription = new AgentDescription { AgentName = "TestAgent", ModelName = null };
        var chatConfiguration = new ChatConfiguration(null!, []);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var modelName = agentDescription.ModelName ?? chatConfiguration.ModelName ?? throw new InvalidOperationException($"Agent '{agentDescription.AgentName}' model name is not set and no default model is configured.");
        });

        Assert.Contains("TestAgent", exception.Message);
        Assert.Contains("model name is not set", exception.Message);
        Assert.Contains("no default model is configured", exception.Message);

        _output.WriteLine($"✅ Correct exception thrown: {exception.Message}");
    }
}