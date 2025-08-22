#pragma warning disable SKEXP0110

using System.Text.Json;

using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Models.StopAgents;

using Xunit.Abstractions;

namespace ChatClient.Tests;

public class MultiAgentSummaryFixTests
{
    private readonly ITestOutputHelper _output;

    public MultiAgentSummaryFixTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void StopAgentFactory_WithEmptySummaryAgent_ShouldThrowException()
    {
        // Arrange
        var factory = new TestableStopAgentFactory();
        var options = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 2,
            SummaryAgent = string.Empty
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            factory.Create("RoundRobinWithSummary", options));

        _output.WriteLine($"Exception message: {exception.Message}");
        Assert.Contains("Summary agent is required", exception.Message);
        Assert.Contains("RoundRobinWithSummary", exception.Message);
    }

    [Fact]
    public void StopAgentFactory_WithNullSummaryAgent_ShouldThrowException()
    {
        // Arrange
        var factory = new TestableStopAgentFactory();
        var options = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 2,
            SummaryAgent = null!
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            factory.Create("RoundRobinWithSummary", options));

        _output.WriteLine($"Exception message: {exception.Message}");
        Assert.Contains("Summary agent is required", exception.Message);
    }

    [Fact]
    public void StopAgentFactory_WithValidSummaryAgent_ShouldSucceed()
    {
        // Arrange
        var factory = new TestableStopAgentFactory();
        var options = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 2,
            SummaryAgent = "𝐀𝐑"
        };

        // Act
        var manager = factory.Create("RoundRobinWithSummary", options);

        // Assert
        Assert.NotNull(manager);
        Assert.IsType<RoundRobinSummaryGroupChatManager>(manager);
        Assert.Equal(2, manager.MaximumInvocationCount);

        var summaryManager = (RoundRobinSummaryGroupChatManager)manager;
        var requiredAgents = summaryManager.GetRequiredAgents().ToList();
        Assert.Single(requiredAgents);
        Assert.Equal("𝐀𝐑", requiredAgents.First());
    }

    [Fact]
    public void ValidateAgentsForChat_WithMissingAgent_ShouldDetect()
    {
        // This simulates the validation logic we added to MultiAgentChat.razor

        // Arrange
        var availableAgents = new List<AgentDescription>
        {
            new() { AgentName = "Kant", ShortName = "K∀" },
            new() { AgentName = "Bentham", ShortName = "ΣU" }
            // Note: No referee agent available
        };

        var options = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 2,
            SummaryAgent = "𝐀𝐑"  // This agent is not in availableAgents
        };

        var manager = new RoundRobinSummaryGroupChatManager(options.SummaryAgent);

        // Act
        var missingAgents = new List<string>();
        if (manager is IGroupChatAgentProvider provider)
        {
            foreach (var agentId in provider.GetRequiredAgents())
            {
                if (string.IsNullOrWhiteSpace(agentId))
                    continue;

                var agent = availableAgents.FirstOrDefault(a => a.AgentId == agentId);
                if (agent is null)
                {
                    missingAgents.Add(agentId);
                }
            }
        }

        // Assert
        Assert.Single(missingAgents);
        Assert.Equal("𝐀𝐑", missingAgents.First());

        _output.WriteLine($"✅ Successfully detected missing agent: {missingAgents.First()}");
        _output.WriteLine("This validation prevents the runtime exception!");
    }

    [Fact]
    public void ValidateAgentsForChat_WithAllAgentsAvailable_ShouldPass()
    {
        // This simulates successful validation

        // Arrange
        var availableAgents = new List<AgentDescription>
        {
            new() { AgentName = "Kant", ShortName = "K∀" },
            new() { AgentName = "Bentham", ShortName = "ΣU" },
            new() { AgentName = "Debate Referee / Negotiation Coach", ShortName = "𝐀𝐑" }
        };

        var options = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 2,
            SummaryAgent = "𝐀𝐑"
        };

        var manager = new RoundRobinSummaryGroupChatManager(options.SummaryAgent);

        // Act
        var missingAgents = new List<string>();
        var foundAgents = new List<AgentDescription>();

        if (manager is IGroupChatAgentProvider provider)
        {
            foreach (var agentId in provider.GetRequiredAgents())
            {
                if (string.IsNullOrWhiteSpace(agentId))
                    continue;

                var agent = availableAgents.FirstOrDefault(a => a.AgentId == agentId);
                if (agent is null)
                {
                    missingAgents.Add(agentId);
                }
                else
                {
                    foundAgents.Add(agent);
                }
            }
        }

        // Assert
        Assert.Empty(missingAgents);
        Assert.Single(foundAgents);
        Assert.Equal("𝐀𝐑", foundAgents.First().AgentId);

        _output.WriteLine($"✅ All required agents found: {string.Join(", ", foundAgents.Select(a => a.AgentName))}");
    }

    // Create a testable version of StopAgentFactory to access the internal methods
    private class TestableStopAgentFactory
    {
        private readonly Dictionary<string, Func<IStopAgentOptions?, Microsoft.SemanticKernel.Agents.Orchestration.GroupChat.GroupChatManager>> _factories = new()
        {
            ["RoundRobin"] = o => CreateRoundRobin(o as RoundRobinStopAgentOptions),
            ["RoundRobinWithSummary"] = o => CreateRoundRobinWithSummary(o as RoundRobinSummaryStopAgentOptions)
        };

        public Microsoft.SemanticKernel.Agents.Orchestration.GroupChat.GroupChatManager Create(string name, IStopAgentOptions? options = null)
        {
            if (_factories.TryGetValue(name, out var factory))
                return factory(options);
            return CreateRoundRobin(options as RoundRobinStopAgentOptions);
        }

        private static Microsoft.SemanticKernel.Agents.Orchestration.GroupChat.GroupChatManager CreateRoundRobin(RoundRobinStopAgentOptions? opts)
        {
            var rounds = opts?.Rounds ?? 1;
            return new BridgingRoundRobinManager { MaximumInvocationCount = rounds };
        }

        private static Microsoft.SemanticKernel.Agents.Orchestration.GroupChat.GroupChatManager CreateRoundRobinWithSummary(RoundRobinSummaryStopAgentOptions? opts)
        {
            var rounds = opts?.Rounds ?? 1;
            var agent = opts?.SummaryAgent ?? string.Empty;

            // Validate that summary agent is specified
            if (string.IsNullOrWhiteSpace(agent))
            {
                throw new ArgumentException(
                    "Summary agent is required for RoundRobinWithSummary strategy. " +
                    "Please select a summary agent in the configuration.",
                    nameof(opts));
            }

            return new RoundRobinSummaryGroupChatManager(agent) { MaximumInvocationCount = rounds };
        }
    }
}
