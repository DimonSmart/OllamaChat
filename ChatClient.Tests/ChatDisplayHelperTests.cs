using ChatClient.Api.Client.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class ChatDisplayHelperTests
{
    [Fact]
    public void GetAvatarText_UsesInitials_ForMultiWordName()
    {
        var avatarText = ChatDisplayHelper.GetAvatarText("Ivan Petrov");

        Assert.Equal("IP", avatarText);
    }

    [Fact]
    public void GetAvatarText_TruncatesSingleWordName()
    {
        var avatarText = ChatDisplayHelper.GetAvatarText("candidate");

        Assert.Equal("CA", avatarText);
    }

    [Fact]
    public void GetAssistantAvatarText_UsesConfiguredShortNameButKeepsItCompact()
    {
        var avatarText = ChatDisplayHelper.GetAssistantAvatarText(
            [
                new AgentExecutionSpec
                {
                    AgentName = "Interview Coach Behavioural",
                    ShortName = "behavioural",
                    RuntimeAgentId = "behavioural"
                }
            ],
            "behavioural");

        Assert.Equal("BEH", avatarText);
    }

    [Fact]
    public void GetAssistantAvatarText_PrefersExplicitAvatarText()
    {
        var avatarText = ChatDisplayHelper.GetAssistantAvatarText(
            [
                new AgentExecutionSpec
                {
                    AgentName = "Immanuel Kant",
                    ShortName = "kant",
                    AvatarText = "K",
                    RuntimeAgentId = "kant"
                }
            ],
            "kant");

        Assert.Equal("K", avatarText);
    }

    [Fact]
    public void GetAssistantAvatarText_FallsBackToAgentNameAbbreviation()
    {
        var avatarText = ChatDisplayHelper.GetAssistantAvatarText(
            [
                new AgentExecutionSpec
                {
                    AgentName = "Interview Coach Technical",
                    ShortName = "technical",
                    RuntimeAgentId = "technical"
                }
            ],
            "technical");

        Assert.Equal("TEC", avatarText);
    }

    [Fact]
    public void AssistantIdentity_FallsBackToMessageMetadata_WhenRuntimeAgentIsNotInOuterAgentList()
    {
        var agents = new[]
        {
            new AgentExecutionSpec
            {
                AgentName = "Scenario Dispute Group Chat",
                RuntimeAgentId = "workflow"
            }
        };

        var displayName = ChatDisplayHelper.GetAssistantDisplayName(
            agents,
            "participant_a",
            "Participant A Advocate");
        var avatarText = ChatDisplayHelper.GetAssistantAvatarText(
            agents,
            "participant_a",
            "Participant A Advocate");

        Assert.Equal("Participant A Advocate", displayName);
        Assert.Equal("PAA", avatarText);
    }
}
