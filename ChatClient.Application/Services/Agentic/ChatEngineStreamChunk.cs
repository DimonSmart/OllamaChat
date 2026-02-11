using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed record ChatEngineStreamChunk(
    string AgentName,
    string Content,
    bool IsFinal = false,
    bool IsError = false,
    IReadOnlyList<FunctionCallRecord>? FunctionCalls = null,
    string? RetrievedContext = null);
