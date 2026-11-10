using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Client.ViewModels;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class SteeringDeliveryChatClientTests
{
    [Fact]
    public async Task StreamingDelivery_IsAcknowledgedOnlyAfterEnumerationReachesModel()
    {
        using var fake = new FakeModelClient();
        var monitor = new SteeringDeliveryMonitor();
        using var client = new SteeringDeliveryChatClient(fake, monitor);
        var id = Guid.NewGuid();
        var delivered = new List<Guid>();
        monitor.Delivered += delivered.Add;
        var message = Tagged(id);

        var stream = client.GetStreamingResponseAsync([message]);
        Assert.Empty(delivered);
        Assert.Equal(0, fake.Calls);

        await foreach (var _ in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(1, fake.Calls);
        Assert.Equal([id], delivered);
    }

    [Fact]
    public async Task StreamingFailureBeforeFirstUpdate_IsNotAcknowledged()
    {
        using var fake = new FakeModelClient { FailBeforeUpdate = true };
        var monitor = new SteeringDeliveryMonitor();
        using var client = new SteeringDeliveryChatClient(fake, monitor);
        var delivered = new List<Guid>();
        monitor.Delivered += delivered.Add;
        var stream = client.GetStreamingResponseAsync([Tagged(Guid.NewGuid())]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in stream.WithCancellation(TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal(1, fake.Calls);
        Assert.Empty(delivered);
    }

    [Fact]
    public async Task OnlyTaggedUserMessagesAreAcknowledged()
    {
        using var fake = new FakeModelClient();
        var monitor = new SteeringDeliveryMonitor();
        using var client = new SteeringDeliveryChatClient(fake, monitor);
        var delivered = new List<Guid>();
        monitor.Delivered += delivered.Add;
        var id = Guid.NewGuid();

        await foreach (var _ in client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.User, "ordinary"),
            Tagged(id),
            new ChatMessage(ChatRole.Assistant, "unrelated")
        ]).WithCancellation(TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal([id], delivered);
    }

    [Fact]
    public void Metadata_IsCopiedAndSerializedWithoutChangingOrdinaryMessages()
    {
        var message = new AppChatMessage("instruction", DateTime.UtcNow, AppChatRole.User)
        {
            Steering = new SteeringMessageMetadata
            {
                SteeringId = Guid.NewGuid(),
                RunId = Guid.NewGuid(),
                Sequence = 2,
                SubmittedAtUtc = DateTime.UtcNow,
                Status = SteeringDeliveryStatus.NotDelivered,
                Reason = "RunCompleted"
            }
        };
        var copy = new AppChatMessage(message);
        Assert.NotSame(message.Steering, copy.Steering);
        Assert.Equal(message.Steering.SteeringId, copy.Steering!.SteeringId);
        Assert.Equal(message.Steering.RunId, copy.Steering.RunId);
        Assert.Equal(message.Steering.Status, AppChatMessageViewModel.CreateFromDomainModel(copy).Steering!.Status);

        var restored = JsonSerializer.Deserialize<AppChatMessage>(JsonSerializer.Serialize(copy));
        Assert.NotNull(restored?.Steering);
        Assert.Equal(message.Steering.Reason, restored.Steering.Reason);
        Assert.Null(JsonSerializer.Deserialize<AppChatMessage>(
            "{\"Content\":\"old transcript\",\"Role\":1}")!.Steering);

        var export = ChatTranscriptFormatter.Format(
            [AppChatMessageViewModel.CreateFromDomainModel(copy)], ChatFormat.Text);
        Assert.Contains("Steering: Not delivered", export, StringComparison.Ordinal);
    }

    private static ChatMessage Tagged(Guid id) => new(ChatRole.User, "steer")
    {
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [SteeringDeliveryChatClient.SteeringIdKey] = id.ToString("N")
        }
    };

    private sealed class FakeModelClient : IChatClient
    {
        public bool FailBeforeUpdate { get; set; }
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            if (FailBeforeUpdate)
                throw new InvalidOperationException("provider unavailable");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
