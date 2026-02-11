namespace ChatClient.Application.Services.Agentic;

public sealed class AgenticToolInvocationPolicyOptions
{
    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 1;

    public int RetryDelayMs { get; set; } = 250;
}
