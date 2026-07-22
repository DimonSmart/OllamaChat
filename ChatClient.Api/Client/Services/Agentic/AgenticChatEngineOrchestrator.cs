using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using System.Text;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticChatEngineOrchestrator(
    IAgenticExecutionRuntime runtime) : IChatEngineOrchestrator
{
    public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        ChatEngineOrchestrationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversation = ToChatMessages(request.Messages);
        if (conversation.Count == 0 || conversation[^1].Role != ChatRole.User)
        {
            conversation.Add(new ChatMessage(ChatRole.User, BuildUserMessage(request.UserMessage, request.Files)));
        }

        var runtimeRequest = request.Agent
            .ForRun()
            .UsingModel(request.ResolvedModel)
            .WithConfiguration(request.Configuration)
            .WithConversation(conversation)
            .WithUserMessage(request.UserMessage)
            .Build();

        await foreach (var chunk in runtime.StreamAsync(runtimeRequest, cancellationToken))
        {
            yield return chunk;
        }
    }

    private static List<ChatMessage> ToChatMessages(IEnumerable<IAppChatMessage> messages) =>
        messages
            .Where(static message => !message.IsStreaming && !string.IsNullOrWhiteSpace(message.Content))
            .Select(static message => new ChatMessage(message.Role.ToAiChatRole(), message.Content))
            .ToList();

    private static string BuildUserMessage(string text, IReadOnlyList<AppChatMessageFile>? files)
    {
        if (files is null || files.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Trim());
        builder.AppendLine().AppendLine().AppendLine("Attached files:");
        foreach (var file in files)
        {
            builder.AppendLine($"- {file.Name} ({file.ContentType}, {file.Size} bytes)");
        }

        return builder.ToString().Trim();
    }
}
