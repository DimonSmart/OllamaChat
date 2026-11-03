using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

/// <summary>
/// Observes each actual inner IChatClient call. The runtime still owns tools, history,
/// compaction, streaming, cancellation and every response/update object.
/// </summary>
public sealed class ContextCaptureChatClient(
    IChatClient inner,
    ContextCaptureStore store,
    string agentName,
    string? agentInstanceId,
    string purpose,
    string defaultModel) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialize a possibly lazy input once, and pass that exact logical set onward.
        // Do not clone or modify any ChatMessage or ChatOptions.
        var input = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var started = Stopwatch.StartNew();
        string? id = TryStart(input, options);
        try
        {
            var response = await base.GetResponseAsync(input, options, cancellationToken).ConfigureAwait(false);
            TryComplete(id, ContextInvocationStatus.Completed, started.Elapsed, response.Usage);
            return response;
        }
        catch (OperationCanceledException)
        {
            TryComplete(id, ContextInvocationStatus.Canceled, started.Elapsed);
            throw;
        }
        catch
        {
            TryComplete(id, ContextInvocationStatus.Failed, started.Elapsed);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The iterator body executes on first MoveNextAsync, not on method invocation.
        var input = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var started = Stopwatch.StartNew();
        string? id = TryStart(input, options);
        ContextInvocationStatus status = ContextInvocationStatus.Canceled;
        UsageDetails? usage = null;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(input, options, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                // Only usage updates are examined; yield the identical update in the same order.
                try
                {
                    foreach (var item in update.Contents)
                        if (item is UsageContent reported)
                        {
                            usage ??= new UsageDetails();
                            usage.Add(reported.Details);
                        }
                }
                catch { /* Provider metadata is best-effort. */ }
                yield return update;
            }
            status = ContextInvocationStatus.Completed;
        }
        finally
        {
            if (status != ContextInvocationStatus.Completed)
                status = cancellationToken.IsCancellationRequested
                    ? ContextInvocationStatus.Canceled
                    : ContextInvocationStatus.Failed;
            TryComplete(id, status, started.Elapsed, usage);
        }
    }

    private string? TryStart(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        try
        {
            var mode = store.Mode;
            if (mode == ContextCaptureMode.Off) return null;
            var (link, traceId, spanId) = ContextCaptureScope.GetChatSpan();
            var invocation = new ContextInvocationSnapshot
            {
                InvocationId = Guid.NewGuid().ToString("N"),
                RunId = ContextCaptureScope.RunId,
                AgentName = agentName,
                AgentInstanceId = agentInstanceId,
                Purpose = ContextCaptureScope.Purpose ?? purpose,
                ModelId = options?.ModelId ?? defaultModel,
                StartedAt = DateTimeOffset.UtcNow,
                CaptureMode = mode,
                LinkState = link,
                TraceId = traceId,
                SpanId = spanId
            };
            return store.Begin(invocation, CaptureSegments(messages, options, mode));
        }
        catch
        {
            // Capture failure must not affect the model invocation or leak prompt content to logs.
            return null;
        }
    }

    private void TryComplete(string? id, ContextInvocationStatus status, TimeSpan duration, UsageDetails? usage = null)
    {
        try
        {
            store.Complete(id, status, duration,
                usage?.InputTokenCount, usage?.OutputTokenCount,
                usage?.CachedInputTokenCount, usage?.ReasoningTokenCount);
        }
        catch { /* Diagnostic failure must not affect the agent. */ }
    }

    internal static IEnumerable<ContextSegmentDraft> CaptureSegments(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options, ContextCaptureMode mode)
    {
        bool full = mode == ContextCaptureMode.FullContext;
        if (!string.IsNullOrEmpty(options?.Instructions))
            yield return Draft(ContextCategory.Instructions, "instructions", "ChatOptions.Instructions",
                null, null, null, options.Instructions!, false, full);

        foreach (var message in messages)
        {
            var role = message.Role.ToString();
            foreach (var content in message.Contents)
            {
                ContextCategory category;
                string? value;
                bool structured = false;
                string? callId = null, toolName = null;
                switch (content)
                {
                    case TextContent text:
                        category = role is "system" or "developer"
                            ? ContextCategory.Instructions : ContextCategory.Conversation;
                        value = text.Text;
                        break;
                    case FunctionCallContent call:
                        category = ContextCategory.ToolCalls;
                        callId = call.CallId;
                        toolName = call.Name;
                        value = SafeJson(call.Arguments);
                        structured = true;
                        break;
                    case FunctionResultContent result:
                        category = ContextCategory.ToolResults;
                        callId = result.CallId;
                        value = SafeJson(result.Result);
                        structured = true;
                        break;
                    default:
                        // Image, audio, opaque binary and custom content have unknown token size.
                        // Never serialize RawRepresentation or binary payloads into the store.
                        yield return new ContextSegmentDraft(
                            ContextCategory.Other, role, content.GetType().Name,
                            message.MessageId, null, null, null, null, false);
                        continue;
                }
                yield return Draft(category, role, content.GetType().Name, message.MessageId,
                    callId, toolName, value, structured, full);
            }
        }

        if (options?.Tools is { } tools)
        {
            foreach (var tool in tools)
            {
                var schema = tool is AIFunctionDeclaration function
                    ? SafeSchema(function) : null;
                var text = $"Name: {tool.Name}\nDescription: {tool.Description}\nSchema: {schema ?? "(unavailable)"}";
                // The schema here is observed on ChatOptions, not reconstructed from a UI tool list.
                yield return Draft(ContextCategory.ToolDefinitions, "tools", tool.GetType().Name,
                    null, null, tool.Name, text, true, full, schema is not null);
            }
        }
    }

    private static ContextSegmentDraft Draft(
        ContextCategory category, string role, string kind, string? messageId, string? callId,
        string? toolName, string? content, bool structured, bool full, bool known = true) =>
        new(category, role, kind, messageId, callId, toolName,
            known && content is not null ? Math.Max(1L, (content.Length + 3L) / 4L) : null,
            full ? content : null, structured);

    private static string? SafeJson(object? value)
    {
        try { return JsonSerializer.Serialize(value); }
        catch { return null; }
    }

    private static string? SafeSchema(AIFunctionDeclaration function)
    {
        try { return function.JsonSchema.GetRawText(); }
        catch { return null; }
    }
}
