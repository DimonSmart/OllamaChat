using ChatClient.Domain.Models;
using Xunit;

namespace ChatClient.Tests;

public class AppChatConfigurationTests
{
    [Fact]
    public void ToString_IncludesWhiteboardSetting()
    {
        var configuration = new AppChatConfiguration("test-model", ["fn1"], UseWhiteboard: false);

        var text = configuration.ToString();

        Assert.Contains("UseWhiteboard = False", text);
        Assert.Contains("fn1", text);
    }
}
