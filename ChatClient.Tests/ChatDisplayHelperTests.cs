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
                new AgentDescription
                {
                    AgentName = "Interview Coach Behavioural",
                    ShortName = "behavioural"
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
                new AgentDescription
                {
                    AgentName = "Immanuel Kant",
                    ShortName = "kant",
                    AvatarText = "K"
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
                new AgentDescription
                {
                    AgentName = "Interview Coach Technical",
                    ShortName = "technical"
                }
            ],
            "technical");

        Assert.Equal("TEC", avatarText);
    }
}
