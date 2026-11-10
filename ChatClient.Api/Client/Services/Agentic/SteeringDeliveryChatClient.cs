using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace ChatClient.Api.Client.Services.Agentic;

/// <summary>
/// Observes actual root-model requests, not the MAF enqueue operation.
/// The outer Harness history persistence retains the injected user message.
/// </summary>
internal sealed class SteeringDeliveryMonitor
{
    public event Action<Guid>? Delivered;
    internal void Notify(Guid id) => Delivered?.Invoke(id);
}

internal sealed class SteeringDeliveryChatClient(IChatClient inner, SteeringDeliveryMonitor monitor)
    : DelegatingChatClient(inner)
{
    internal const string SteeringIdKey = "OllamaChat.SteeringId";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var input = messages.ToArray();
        var response = await base.GetResponseAsync(input, options, cancellationToken);
        Notify(input);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = messages.ToArray();
        await using var enumerator = base.GetStreamingResponseAsync(input, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var notified = false;
        while (await enumerator.MoveNextAsync())
        {
            if (!notified)
            {
                Notify(input);
                notified = true;
            }
            yield return enumerator.Current;
        }
        if (!notified)
            Notify(input);
    }

    private void Notify(IEnumerable<ChatMessage> input)
    {
        foreach (var message in input)
        {
            if (message.Role != ChatRole.User ||
                message.AdditionalProperties?.TryGetValue(SteeringIdKey, out var value) != true ||
                !Guid.TryParse(value?.ToString(), out var id))
                continue;
            monitor.Notify(id);
        }
    }
}
