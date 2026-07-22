using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class HarnessResponseEventProjector(ILogger<HarnessResponseEventProjector> logger)
{
    internal Projection CreateProjection() => new(logger);

    internal sealed class Projection(ILogger logger)
    {
        private readonly Dictionary<string, PendingToolCall> _pendingCalls = new(StringComparer.Ordinal);

        public IReadOnlyList<HarnessResponseEvent> Project(
            AgentResponseUpdate update,
            IReadOnlyDictionary<string, AgenticRegisteredTool> metadataByName)
        {
            ArgumentNullException.ThrowIfNull(update);
            List<HarnessResponseEvent> events = [];

            if (!string.IsNullOrWhiteSpace(update.ResponseId))
            {
                events.Add(new HarnessResponseMetadata(update.ResponseId, null));
            }

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        events.Add(new HarnessTextDelta(text.Text));
                        break;

                    case FunctionCallContent call:
                        events.Add(ProjectCall(call, metadataByName, update.CreatedAt));
                        break;

                    case FunctionResultContent result:
                        events.Add(ProjectResult(result, update.CreatedAt));
                        break;

                    default:
                        logger.LogDebug(
                            "Ignoring unsupported Harness response content type {ContentType}.",
                            content.GetType().FullName);
                        break;
                }
            }

            return events;
        }

        private HarnessToolCallStarted ProjectCall(
            FunctionCallContent call,
        IReadOnlyDictionary<string, AgenticRegisteredTool> metadataByName,
        DateTimeOffset? createdAt)
        {
            var startedAt = createdAt ?? DateTimeOffset.UtcNow;
            if (call.RawRepresentation is ToolInvocationViewState projected)
            {
                var projectedPending = new PendingToolCall(
                    projected.CallId,
                    projected.RegisteredName,
                    projected.OriginalName,
                    projected.Source,
                    projected.ServerName,
                    projected.BindingName,
                    projected.IsInteractive,
                    projected.Arguments,
                    projected.StartedAt);
                _pendingCalls[projected.CallId] = projectedPending;
                return new HarnessToolCallStarted(
                    projectedPending.CallId,
                    projectedPending.RegisteredName,
                    projectedPending.OriginalName,
                    projectedPending.Source,
                    projectedPending.ServerName,
                    projectedPending.BindingName,
                    projectedPending.IsInteractive,
                    projectedPending.Arguments,
                    projectedPending.StartedAt);
            }

            metadataByName.TryGetValue(call.Name, out var metadata);
            var pending = new PendingToolCall(
                call.CallId,
                call.Name,
                metadata?.ToolName ?? call.Name,
                metadata?.Source ?? "unknown",
                metadata?.ServerName ?? "unknown",
                metadata?.BindingName,
                metadata?.MayRequireUserInput ?? false,
                Serialize(call.Arguments),
                startedAt);
            _pendingCalls[call.CallId] = pending;

            return new HarnessToolCallStarted(
                pending.CallId,
                pending.RegisteredName,
                pending.OriginalName,
                pending.Source,
                pending.ServerName,
                pending.BindingName,
                pending.IsInteractive,
                pending.Arguments,
                pending.StartedAt);
        }

        private HarnessResponseEvent ProjectResult(FunctionResultContent result, DateTimeOffset? createdAt)
        {
            var completedAt = createdAt ?? DateTimeOffset.UtcNow;
            if (result.RawRepresentation is ToolInvocationViewState projected)
            {
                _pendingCalls.Remove(projected.CallId);
                return projected.Status == ToolInvocationStatus.Succeeded
                    ? new HarnessToolCallCompleted(
                        projected.CallId, projected.RegisteredName, projected.OriginalName,
                        projected.Source, projected.ServerName, projected.BindingName,
                        projected.IsInteractive, projected.Arguments, projected.Result ?? "null",
                        projected.StartedAt, projected.CompletedAt ?? completedAt)
                    : new HarnessToolCallFailed(
                        projected.CallId, projected.RegisteredName, projected.OriginalName,
                        projected.Source, projected.ServerName, projected.BindingName,
                        projected.IsInteractive, projected.Arguments,
                        projected.Error ?? "Tool invocation failed.", projected.StartedAt,
                        projected.CompletedAt ?? completedAt);
            }

            if (!_pendingCalls.Remove(result.CallId, out var pending))
            {
                pending = new PendingToolCall(
                    result.CallId,
                    "unknown",
                    "unknown",
                    "unknown",
                    "unknown",
                    null,
                    false,
                    "{}",
                    completedAt);
            }

            if (result.Exception is { } exception)
            {
                return new HarnessToolCallFailed(
                    pending.CallId,
                    pending.RegisteredName,
                    pending.OriginalName,
                    pending.Source,
                    pending.ServerName,
                    pending.BindingName,
                    pending.IsInteractive,
                    pending.Arguments,
                    exception.Message,
                    pending.StartedAt,
                    completedAt);
            }

            return new HarnessToolCallCompleted(
                pending.CallId,
                pending.RegisteredName,
                pending.OriginalName,
                pending.Source,
                pending.ServerName,
                pending.BindingName,
                pending.IsInteractive,
                pending.Arguments,
                Serialize(result.Result),
                pending.StartedAt,
                completedAt);
        }

        private static string Serialize(object? value)
        {
            try
            {
                return JsonSerializer.Serialize(value);
            }
            catch
            {
                return value?.ToString() ?? "null";
            }
        }

        private sealed record PendingToolCall(
        string CallId,
        string RegisteredName,
        string OriginalName,
        string Source,
        string ServerName,
        string? BindingName,
        bool IsInteractive,
        string Arguments,
            DateTimeOffset StartedAt);
    }
}
