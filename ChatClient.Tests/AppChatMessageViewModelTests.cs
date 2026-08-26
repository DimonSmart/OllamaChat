using ChatClient.Api.Client.ViewModels;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class AppChatMessageViewModelTests
{
    [Fact]
    public void UpdateFromDomainModel_MapsContentAndStatisticsIndependently()
    {
        var message = new AppChatMessage(
            "**Response**",
            DateTime.UtcNow,
            AppChatRole.Assistant,
            statistics: "technical metadata");

        var viewModel = new AppChatMessageViewModel().UpdateFromDomainModel(message);

        Assert.Equal(message.Content, viewModel.RawContent);
        Assert.Equal(message.Content, viewModel.Content);
        Assert.Equal(message.Statistics, viewModel.Statistics);
        Assert.NotEqual(viewModel.RawContent, viewModel.Statistics);
    }
}
