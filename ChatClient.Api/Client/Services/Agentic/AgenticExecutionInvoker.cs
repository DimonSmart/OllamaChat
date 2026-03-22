using System.Text;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticExecutionInvoker(IAgenticExecutionRuntime runtime) : IAgenticExecutionInvoker
{
    public async Task<AgenticExecutionInvocationResult> InvokeAsync(
        AgenticExecutionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        StringBuilder response = new();
        List<FunctionCallRecord> functionCalls = [];

        await foreach (var chunk in runtime.StreamAsync(request, cancellationToken))
        {
            if (chunk.FunctionCalls is { Count: > 0 })
                functionCalls.AddRange(chunk.FunctionCalls);

            if (!string.IsNullOrEmpty(chunk.Content))
                response.Append(chunk.Content);

            if (chunk.IsError)
            {
                return new AgenticExecutionInvocationResult(
                    FinalText: response.ToString(),
                    IsError: true,
                    ErrorMessage: string.IsNullOrWhiteSpace(chunk.Content) ? "Agent execution failed." : chunk.Content.Trim(),
                    FunctionCalls: functionCalls);
            }

            if (chunk.IsFinal)
                break;
        }

        return new AgenticExecutionInvocationResult(
            FinalText: response.ToString(),
            IsError: false,
            ErrorMessage: null,
            FunctionCalls: functionCalls);
    }
}
