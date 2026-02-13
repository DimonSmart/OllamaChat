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

        var prompt = BuildPrompt(serverName, request);
        var handler = ResolveElicitationHandler();

        if (handler is null)
        {
            logger.LogWarning(
                "No elicitation handler is registered. Returning cancel for request from server {ServerName}.",
                serverName);
            return CreateResult(McpElicitationResponse.Cancel);
        }

        try
        {
            var response = await handler(prompt, cancellationToken);
            return CreateResult(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Elicitation flow canceled by UI handler for server {ServerName}.",
                serverName);
            return CreateResult(McpElicitationResponse.Cancel);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to process elicitation request from server {ServerName}. Returning cancel.",
                serverName);
            return CreateResult(McpElicitationResponse.Cancel);
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

    private static McpElicitationPrompt BuildPrompt(string serverName, ElicitRequestParams request)
    {
        var requestJson = JsonSerializer.SerializeToElement(request);

        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? ReadString(requestJson, "mode") ?? "form"
            : request.Mode;
        var message = string.IsNullOrWhiteSpace(request.Message)
            ? ReadString(requestJson, "message") ?? "MCP server requested additional user input."
            : request.Message;
        var url = string.IsNullOrWhiteSpace(request.Url)
            ? ReadString(requestJson, "url")
            : request.Url;

        var elicitationId = ReadString(requestJson, "elicitationId");
        var fields = string.Equals(mode, "form", StringComparison.OrdinalIgnoreCase)
            ? ParseFields(requestJson)
            : [];

        return new McpElicitationPrompt(serverName, mode, message, url, elicitationId, fields);
    }

    private static IReadOnlyList<McpElicitationField> ParseFields(JsonElement requestJson)
    {
        if (!TryGetPropertyIgnoreCase(requestJson, "requestedSchema", out var schema) ||
            schema.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var requiredFields = new HashSet<string>(StringComparer.Ordinal);
        if (TryGetPropertyIgnoreCase(schema, "required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var fieldName = item.GetString();
                    if (!string.IsNullOrWhiteSpace(fieldName))
                    {
                        requiredFields.Add(fieldName);
                    }
                }
            }
        }

        if (TryGetPropertyIgnoreCase(schema, "properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            var fields = new List<McpElicitationField>();
            foreach (var property in properties.EnumerateObject())
            {
                fields.Add(ParseField(
                    property.Name,
                    property.Value,
                    requiredFields.Contains(property.Name)));
            }

            return fields;
        }

        if (TryGetPropertyIgnoreCase(schema, "type", out _) ||
            TryGetPropertyIgnoreCase(schema, "enum", out _) ||
            TryGetPropertyIgnoreCase(schema, "oneOf", out _) ||
            TryGetPropertyIgnoreCase(schema, "anyOf", out _))
        {
            return [ParseField("value", schema, requiredFields.Contains("value"))];
        }

        return [];
    }

    private static McpElicitationField ParseField(string name, JsonElement schema, bool required)
    {
        var options = ParseOptions(schema, out var isMultiSelect);

        var type = ReadString(schema, "type")?.ToLowerInvariant();
        if (isMultiSelect)
        {
            type = "array";
        }
        else if (string.IsNullOrWhiteSpace(type))
        {
            type = options.Count > 0 ? "string" : "string";
        }

        JsonElement? defaultValue = null;
        if (TryGetPropertyIgnoreCase(schema, "default", out var schemaDefault))
        {
            defaultValue = schemaDefault.Clone();
        }

        var label = ReadString(schema, "title");
        if (string.IsNullOrWhiteSpace(label))
        {
            label = name;
        }

        return new McpElicitationField(
            Name: name,
            Label: label,
            Type: type ?? "string",
            Description: ReadString(schema, "description"),
            Required: required,
            IsMultiSelect: isMultiSelect,
            Options: options,
            DefaultValue: defaultValue,
            MinItems: ReadInt(schema, "minItems"),
            MaxItems: ReadInt(schema, "maxItems"));
    }

    private static IReadOnlyList<McpElicitationOption> ParseOptions(JsonElement schema, out bool isMultiSelect)
    {
        var options = new List<McpElicitationOption>();
        var uniqueValues = new HashSet<string>(StringComparer.Ordinal);

        isMultiSelect = string.Equals(ReadString(schema, "type"), "array", StringComparison.OrdinalIgnoreCase);
        if (isMultiSelect && TryGetPropertyIgnoreCase(schema, "items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            AddOptionsFromConstArrays(items, "oneOf", options, uniqueValues);
            AddOptionsFromConstArrays(items, "anyOf", options, uniqueValues);
            AddOptionsFromEnum(items, options, uniqueValues);
            return options;
        }

        AddOptionsFromConstArrays(schema, "oneOf", options, uniqueValues);
        AddOptionsFromConstArrays(schema, "anyOf", options, uniqueValues);
        AddOptionsFromEnum(schema, options, uniqueValues);

        return options;
    }

    private static void AddOptionsFromConstArrays(
        JsonElement source,
        string propertyName,
        List<McpElicitationOption> target,
        HashSet<string> uniqueValues)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var optionsArray) ||
            optionsArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var option in optionsArray.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
                continue;

            if (!TryGetPropertyIgnoreCase(option, "const", out var constValue))
                continue;

            var value = JsonElementToString(constValue);
            if (string.IsNullOrWhiteSpace(value) || !uniqueValues.Add(value))
                continue;

            var label = ReadString(option, "title");
            target.Add(new McpElicitationOption(value, string.IsNullOrWhiteSpace(label) ? value : label));
        }
    }

    private static void AddOptionsFromEnum(
        JsonElement source,
        List<McpElicitationOption> target,
        HashSet<string> uniqueValues)
    {
        if (!TryGetPropertyIgnoreCase(source, "enum", out var enumArray) ||
            enumArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        List<string>? enumNames = null;
        if (TryGetPropertyIgnoreCase(source, "enumNames", out var enumNamesArray) &&
            enumNamesArray.ValueKind == JsonValueKind.Array)
        {
            enumNames = enumNamesArray.EnumerateArray()
                .Select(JsonElementToString)
                .ToList();
        }

        var index = 0;
        foreach (var enumValue in enumArray.EnumerateArray())
        {
            var value = JsonElementToString(enumValue);
            if (string.IsNullOrWhiteSpace(value) || !uniqueValues.Add(value))
            {
                index++;
                continue;
            }

            string label = value;
            if (enumNames is { Count: > 0 } && index < enumNames.Count && !string.IsNullOrWhiteSpace(enumNames[index]))
            {
                label = enumNames[index];
            }

            target.Add(new McpElicitationOption(value, label));
            index++;
        }
    }

    private static int? ReadInt(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;

        return null;
    }

    private static string? ReadString(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement source, string propertyName, out JsonElement value)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in source.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static ElicitResult CreateResult(McpElicitationResponse response)
    {
        string action = NormalizeAction(response.Action);
        var payload = new Dictionary<string, object?>
        {
            ["action"] = action
        };

        if (string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedContent = NormalizeContent(response.Content);
            if (normalizedContent is { Count: > 0 })
            {
                payload["content"] = normalizedContent;
            }
        }

        var result = JsonSerializer.Deserialize<ElicitResult>(JsonSerializer.Serialize(payload));
        if (result is not null)
        {
            return result;
        }

        return JsonSerializer.Deserialize<ElicitResult>("{\"action\":\"cancel\"}")!;
    }

    private static string NormalizeAction(string? action)
    {
        return action?.ToLowerInvariant() switch
        {
            "accept" => "accept",
            "decline" => "decline",
            _ => "cancel"
        };
    }

    private static IReadOnlyDictionary<string, object?>? NormalizeContent(IReadOnlyDictionary<string, object?>? content)
    {
        if (content is null || content.Count == 0)
            return null;

        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in content)
        {
            normalized[key] = NormalizeContentValue(value);
        }

        return normalized;
    }

    private static object? NormalizeContentValue(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonElement element)
            return NormalizeJsonElement(element);

        if (value is bool or string or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return value;

        if (value is IEnumerable<string> stringEnumerable && value is not string)
            return stringEnumerable.ToArray();

        if (value is IEnumerable<object?> objectEnumerable && value is not string)
            return objectEnumerable.Select(NormalizeContentValue).ToArray();

        return value.ToString();
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var whole)
                ? whole
                : element.TryGetDouble(out var fractional) ? fractional : element.GetRawText(),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJsonElement).ToArray(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static string JsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };
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
