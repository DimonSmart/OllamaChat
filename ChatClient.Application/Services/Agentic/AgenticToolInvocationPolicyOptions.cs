namespace ChatClient.Application.Services.Agentic;

public sealed class AgenticToolInvocationPolicyOptions
{
    public int TimeoutSeconds { get; set; } = 30;

    public int InteractiveTimeoutSeconds { get; set; } = 180;

    public int MaxRetries { get; set; } = 1;

    public int RetryDelayMs { get; set; } = 250;
}
