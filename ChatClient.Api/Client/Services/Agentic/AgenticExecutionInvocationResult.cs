using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed record AgenticExecutionInvocationResult(
    string FinalText,
    bool IsError,
    string? ErrorMessage,
    IReadOnlyList<FunctionCallRecord> FunctionCalls);
