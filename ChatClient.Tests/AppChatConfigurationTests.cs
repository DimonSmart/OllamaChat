using ChatClient.Domain.Models;
using Xunit;

namespace ChatClient.Tests;

public class AppChatConfigurationTests
{
    [Fact]
    public void ToString_IncludesFunctionsAndMcpBindingCount()
    {
        var configuration = new AppChatConfiguration("test-model", ["fn1"]);

        var text = configuration.ToString();

        Assert.Contains("fn1", text);
        Assert.Contains("McpBindings = 0", text);
    }
}
