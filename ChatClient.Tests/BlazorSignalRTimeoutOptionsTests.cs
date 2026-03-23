using ChatClient.Api;

namespace ChatClient.Tests;

public class BlazorSignalRTimeoutOptionsTests
{
    [Fact]
    public void Normalize_ClampsTimeoutsToValidSignalRValues()
    {
        var options = new BlazorSignalRTimeoutOptions
        {
            ServerTimeoutSeconds = 10,
            ClientTimeoutSeconds = 20,
            HandshakeTimeoutSeconds = 5,
            KeepAliveIntervalSeconds = 40
        };

        var normalized = options.Normalize();

        Assert.Equal(80, normalized.ServerTimeoutSeconds);
        Assert.Equal(80, normalized.ClientTimeoutSeconds);
        Assert.Equal(15, normalized.HandshakeTimeoutSeconds);
        Assert.Equal(40, normalized.KeepAliveIntervalSeconds);
    }
}
