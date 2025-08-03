using ChatClient.Api.Services;
using ChatClient.Shared.Models;

namespace ChatClient.Tests;

public class ChatCompletionAgentCoordinatorTests
{
    [Fact]
    public void GetNextAgent_RotatesThroughAgents()
    {
        var prompt1 = new SystemPrompt { Name = "a1", Content = "test" };
        var prompt2 = new SystemPrompt { Name = "a2", Content = "test" };
        var coordinator = new ChatCompletionAgentCoordinator(new[] { prompt1, prompt2 });

        Assert.Same(prompt1, coordinator.GetNextAgentPrompt());
        Assert.Same(prompt2, coordinator.GetNextAgentPrompt());
        Assert.Same(prompt1, coordinator.GetNextAgentPrompt());
    }

    [Fact]
    public void ShouldContinueConversation_EnforcesMaxCycles()
    {
        var prompt = new SystemPrompt { Name = "agent" };
        var coordinator = new ChatCompletionAgentCoordinator(new[] { prompt }, maxCyclesWithoutUser: 2);

        Assert.True(coordinator.ShouldContinueConversation(1));
        Assert.False(coordinator.ShouldContinueConversation(2));
    }
}
