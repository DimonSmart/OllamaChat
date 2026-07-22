namespace ChatClient.Application.Services.Agentic;

public sealed record ChatEngineStreamChunk(
    string AgentName,
    string Content,
    bool IsFinal = false,
    bool IsError = false,
    HarnessResponseEvent? Event = null);
