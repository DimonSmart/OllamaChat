using ChatClient.Api.Client.Services.Agentic;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace ChatClient.Api.Services;

/// <summary>
/// Uses the primary LLM for summarization without claiming ownership of it.
/// Only the ambient diagnostic purpose changes; the same messages, options and
/// stream updates continue to the existing IChatClient unchanged.
/// </summary>
internal sealed class CompactionPurposeChatClient(IChatClient inner) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = ContextCaptureScope.BeginPurpose("compaction");
        return await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scope = ContextCaptureScope.BeginPurpose("compaction");
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    // The primary client remains owned by AgentRuntimeResources.
    public void Dispose() { }
}
