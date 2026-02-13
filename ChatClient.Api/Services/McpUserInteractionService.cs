using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ChatClient.Api.Services;

public enum McpInteractionScope
{
    Chat,
    Playground
}

public sealed record McpElicitationOption(string Value, string Label);

public sealed record McpElicitationField(
    string Name,
    string Label,
    string Type,
    string? Description,
    bool Required,
    bool IsMultiSelect,
    IReadOnlyList<McpElicitationOption> Options,
    JsonElement? DefaultValue,
    int? MinItems,
    int? MaxItems);

public sealed record McpElicitationPrompt(
    string ServerName,
    string Mode,
    string Message,
    string? Url,
    string? ElicitationId,
    IReadOnlyList<McpElicitationField> Fields);

public sealed record McpElicitationResponse(string Action, IReadOnlyDictionary<string, object?>? Content)
{
    public static McpElicitationResponse Accept(IReadOnlyDictionary<string, object?>? content = null) => new("accept", content);
    public static McpElicitationResponse Decline { get; } = new("decline", null);
    public static McpElicitationResponse Cancel { get; } = new("cancel", null);
}

public interface IMcpUserInteractionService
{
    IDisposable BeginInteractionScope(McpInteractionScope scope);

    IDisposable RegisterElicitationHandler(
        McpInteractionScope scope,
        Func<McpElicitationPrompt, CancellationToken, Task<McpElicitationResponse>> handler);

    ValueTask<ElicitResult> HandleElicitationAsync(
        string serverName,
        ElicitRequestParams request,
        CancellationToken cancellationToken = default);
}

public sealed class McpUserInteractionService(ILogger<McpUserInteractionService> logger) : IMcpUserInteractionService
{
    private readonly object _gate = new();
    private readonly AsyncLocal<ScopeFrame?> _currentScope = new();
    private readonly Dictionary<McpInteractionScope, List<HandlerRegistration>> _handlersByScope = [];
    private readonly Dictionary<McpInteractionScope, int> _activeScopes = [];
    private long _nextHandlerId;
    private McpInteractionScope? _lastActiveScope;

    public IDisposable BeginInteractionScope(McpInteractionScope scope)
    {
        var previous = _currentScope.Value;
        _currentScope.Value = new ScopeFrame(scope, previous);

        lock (_gate)
        {
            _activeScopes.TryGetValue(scope, out var count);
            _activeScopes[scope] = count + 1;
            _lastActiveScope = scope;
        }

        return new DelegateDisposable(() =>
        {
            _currentScope.Value = previous;

            lock (_gate)
            {
                if (!_activeScopes.TryGetValue(scope, out var count))
                    return;

                if (count <= 1)
                {
                    _activeScopes.Remove(scope);
                }
                else
                {
                    _activeScopes[scope] = count - 1;
                }
            }
        });
    }

    public IDisposable RegisterElicitationHandler(
        McpInteractionScope scope,
        Func<McpElicitationPrompt, CancellationToken, Task<McpElicitationResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var registration = new HandlerRegistration(
            Id: Interlocked.Increment(ref _nextHandlerId),
            Handler: handler);

        lock (_gate)
        {
            if (!_handlersByScope.TryGetValue(scope, out var registrations))
            {
                registrations = [];
                _handlersByScope[scope] = registrations;
            }

            registrations.Add(registration);
            _lastActiveScope = scope;
        }

        return new DelegateDisposable(() =>
        {
            lock (_gate)
            {
                if (!_handlersByScope.TryGetValue(scope, out var registrations))
                    return;

                registrations.RemoveAll(r => r.Id == registration.Id);
                if (registrations.Count == 0)
                {
                    _handlersByScope.Remove(scope);
                }
            }
        });
    }

    public async ValueTask<ElicitResult> HandleElicitationAsync(
        string serverName,
        ElicitRequestParams request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = McpElicitationPromptFactory.Build(serverName, request);
        var handler = ResolveElicitationHandler();

        if (handler is null)
        {
            logger.LogWarning(
                "No elicitation handler is registered. Returning cancel for request from server {ServerName}.",
                serverName);
            return McpElicitResultFactory.Create(McpElicitationResponse.Cancel);
        }

        try
        {
            var response = await handler(prompt, cancellationToken);
            return McpElicitResultFactory.Create(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Elicitation flow canceled by UI handler for server {ServerName}.",
                serverName);
            return McpElicitResultFactory.Create(McpElicitationResponse.Cancel);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to process elicitation request from server {ServerName}. Returning cancel.",
                serverName);
            return McpElicitResultFactory.Create(McpElicitationResponse.Cancel);
        }
    }

    private Func<McpElicitationPrompt, CancellationToken, Task<McpElicitationResponse>>? ResolveElicitationHandler()
    {
        lock (_gate)
        {
            var scoped = _currentScope.Value?.Scope;
            if (scoped is McpInteractionScope scopedScope &&
                TryGetLatestHandlerLocked(scopedScope, out var scopedHandler))
            {
                return scopedHandler;
            }

            McpInteractionScope? singleActiveScope = null;
            foreach (var (scope, count) in _activeScopes)
            {
                if (count <= 0 || !_handlersByScope.ContainsKey(scope))
                    continue;

                if (singleActiveScope is not null)
                {
                    singleActiveScope = null;
                    break;
                }

                singleActiveScope = scope;
            }

            if (singleActiveScope is McpInteractionScope activeScope &&
                TryGetLatestHandlerLocked(activeScope, out var activeHandler))
            {
                return activeHandler;
            }

            if (_lastActiveScope is McpInteractionScope lastScope &&
                TryGetLatestHandlerLocked(lastScope, out var lastHandler))
            {
                return lastHandler;
            }

            if (TryGetLatestHandlerLocked(McpInteractionScope.Chat, out var chatHandler))
                return chatHandler;

            if (TryGetLatestHandlerLocked(McpInteractionScope.Playground, out var playgroundHandler))
                return playgroundHandler;

            return null;
        }
    }

    private bool TryGetLatestHandlerLocked(
        McpInteractionScope scope,
        out Func<McpElicitationPrompt, CancellationToken, Task<McpElicitationResponse>>? handler)
    {
        handler = null;

        if (!_handlersByScope.TryGetValue(scope, out var handlers) || handlers.Count == 0)
            return false;

        handler = handlers[^1].Handler;
        return true;
    }

    private sealed record ScopeFrame(McpInteractionScope Scope, ScopeFrame? Parent);

    private sealed record HandlerRegistration(
        long Id,
        Func<McpElicitationPrompt, CancellationToken, Task<McpElicitationResponse>> Handler);

    private sealed class DelegateDisposable(Action disposeAction) : IDisposable
    {
        private Action? _disposeAction = disposeAction;

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposeAction, null)?.Invoke();
        }
    }
}
