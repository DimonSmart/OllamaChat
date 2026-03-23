namespace ChatClient.Api;

public sealed class BlazorSignalRTimeoutOptions
{
    public const string SectionName = "BlazorSignalR";

    public int ServerTimeoutSeconds { get; set; } = 300;

    public int ClientTimeoutSeconds { get; set; } = 300;

    public int HandshakeTimeoutSeconds { get; set; } = 60;

    public int KeepAliveIntervalSeconds { get; set; } = 15;

    public TimeSpan ServerTimeout => TimeSpan.FromSeconds(ServerTimeoutSeconds);

    public TimeSpan ClientTimeout => TimeSpan.FromSeconds(ClientTimeoutSeconds);

    public TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(HandshakeTimeoutSeconds);

    public TimeSpan KeepAliveInterval => TimeSpan.FromSeconds(KeepAliveIntervalSeconds);

    public int ServerTimeoutMilliseconds => checked(ServerTimeoutSeconds * 1000);

    public int KeepAliveIntervalMilliseconds => checked(KeepAliveIntervalSeconds * 1000);

    public BlazorSignalRTimeoutOptions Normalize()
    {
        var keepAliveIntervalSeconds = Math.Max(5, KeepAliveIntervalSeconds);
        var minTimeoutSeconds = keepAliveIntervalSeconds * 2;

        return new BlazorSignalRTimeoutOptions
        {
            ServerTimeoutSeconds = Math.Max(ServerTimeoutSeconds, minTimeoutSeconds),
            ClientTimeoutSeconds = Math.Max(ClientTimeoutSeconds, minTimeoutSeconds),
            HandshakeTimeoutSeconds = Math.Max(HandshakeTimeoutSeconds, 15),
            KeepAliveIntervalSeconds = keepAliveIntervalSeconds
        };
    }

    public static BlazorSignalRTimeoutOptions FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration
            .GetSection(SectionName)
            .Get<BlazorSignalRTimeoutOptions>();

        return (configured ?? new BlazorSignalRTimeoutOptions()).Normalize();
    }
}
