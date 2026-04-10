using ChatClient.Api.Client.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class AgentSelectionHelperTests
{
    [Fact]
    public void FindByName_ReturnsMatchingAgent_IgnoringCase()
    {
        List<AgentTemplateDefinition> agents =
        [
            new() { AgentName = "Planner" },
            new() { AgentName = "default" }
        ];

        var result = AgentSelectionHelper.FindByName(agents, "Default");

        Assert.NotNull(result);
        Assert.Equal("default", result!.AgentName);
    }

    [Fact]
    public void FindByName_ReturnsNull_WhenAgentIsMissing()
    {
        List<AgentTemplateDefinition> agents =
        [
            new() { AgentName = "Planner" }
        ];

        var result = AgentSelectionHelper.FindByName(agents, "Default");

        Assert.Null(result);
    }

    [Fact]
    public void FindByName_ReturnsNull_WhenRequestedNameIsBlank()
    {
        List<AgentTemplateDefinition> agents =
        [
            new() { AgentName = "Default" }
        ];

        var result = AgentSelectionHelper.FindByName(agents, " ");

        Assert.Null(result);
    }
}
