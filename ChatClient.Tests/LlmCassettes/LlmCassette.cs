using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests.LlmCassettes;

/// <summary>A single-scenario, strictly ordered test cassette. Never used in production.</summary>
internal sealed class LlmCassette
{
    private const string RootToken = "{{WORKSPACE_ROOT}}";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _file;
    private readonly string? _root;
    private readonly bool _record;
    private readonly List<Exchange> _exchanges;
    private int _position;
    private int _active;
    private bool _failed;
    private bool _completed;
    private int _created;

    private sealed record Exchange(string Method, JsonNode Request, JsonNode Response);
    private sealed record CassetteFile(int SchemaVersion, List<Exchange> Exchanges);

    public LlmCassette(string file, bool record = false, string? workspaceRoot = null)
    {
        _file = Path.GetFullPath(file);
        _record = record;
        _root = workspaceRoot?.TrimEnd('/', '\\').Replace('\\', '/');
        if (record)
        {
            _exchanges = [];
        }
        else
        {
            if (!File.Exists(_file))
                throw new FileNotFoundException("Missing LLM cassette (Replay never falls back to Record).", _file);
            var saved = JsonSerializer.Deserialize<CassetteFile>(File.ReadAllText(_file), JsonOptions)
                ?? throw new InvalidDataException("Invalid LLM cassette.");
            if (saved.SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported LLM cassette schemaVersion: {saved.SchemaVersion}");
            _exchanges = saved.Exchanges ?? throw new InvalidDataException("Missing cassette exchanges.");
        }
    }

    public bool IsRecording => _record;

    public IDisposable Enter()
    {
        if (Interlocked.Exchange(ref _active, 1) != 0)
            throw new InvalidOperationException("Concurrent LLM cassette requests are not supported.");
        if (_failed || _completed)
        {
            Interlocked.Exchange(ref _active, 0);
            throw new InvalidOperationException("LLM cassette has already failed or completed.");
        }
        return new ExitScope(this);
    }

    private sealed class ExitScope(LlmCassette owner) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref owner._active, 0);
    }

    public void ClaimClient()
    {
        if (Interlocked.Exchange(ref _created, 1) != 0)
            throw new InvalidOperationException("A second IChatClient was requested for the same cassette.");
    }

    public void Fail() => _failed = true;

    public JsonNode CaptureRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var result = new JsonObject
        {
            ["messages"] = CaptureMessages(messages),
            ["options"] = CaptureOptions(options),
            ["tools"] = CaptureTools(options?.Tools)
        };
        Normalize(result);
        return result;
    }

    public void Add(string method, JsonNode request, JsonNode response)
    {
        if (!_record) throw new InvalidOperationException("Cannot record in Replay mode.");
        Normalize(response);
        _exchanges.Add(new Exchange(method, request.DeepClone(), response.DeepClone()));
    }

    public JsonNode Next(string method, JsonNode request)
    {
        if (_record) throw new InvalidOperationException("Cannot replay in Record mode.");
        var number = _position + 1;
        if (_position == _exchanges.Count)
            throw new InvalidOperationException($"LLM cassette extra call at exchange {number}.");
        var expected = _exchanges[_position];
        if (expected.Method != method)
            throw new InvalidOperationException($"LLM cassette mismatch\\nExchange: {number}\\nPath: method\\nExpected: {expected.Method}\\nActual: {method}");
        var difference = FirstDifference(expected.Request, request, "request");
        if (difference is not null)
        {
            _failed = true;
            throw new InvalidOperationException($"LLM cassette mismatch\\nExchange: {number}\\n{difference}");
        }
        _position++;
        var copy = expected.Response.DeepClone();
        Restore(copy);
        return copy;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) throw new InvalidOperationException("LLM cassette has already completed.");
        if (_failed || Volatile.Read(ref _active) != 0)
            throw new InvalidOperationException("LLM cassette failed or has an active request.");
        if (Volatile.Read(ref _created) == 0) throw new InvalidOperationException("No cassette client was created.");
        if (!_record && _position != _exchanges.Count)
            throw new InvalidOperationException($"LLM cassette has {_exchanges.Count - _position} unused exchanges.");
        _completed = true;
        if (!_record) return;

        var directory = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_file)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var contents = JsonSerializer.Serialize(new CassetteFile(1, _exchanges), JsonOptions);
            await File.WriteAllTextAsync(temporary, contents + Environment.NewLine, cancellationToken);
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static JsonNode CaptureResponse(ChatResponse response) => new JsonObject
    {
        ["messages"] = CaptureMessages(response.Messages),
        ["responseId"] = response.ResponseId,
        ["conversationId"] = response.ConversationId,
        ["continuationToken"] = response.ContinuationToken is null ? null : Convert.ToBase64String(response.ContinuationToken.ToBytes().Span),
        ["modelId"] = response.ModelId,
        ["finishReason"] = response.FinishReason?.Value
    };

    public static ChatResponse RestoreResponse(JsonNode node)
    {
        var obj = node.AsObject();
        return new ChatResponse(ReadMessages(obj["messages"]))
        {
            ResponseId = ReadString(obj, "responseId"),
            ConversationId = ReadString(obj, "conversationId"),
            ContinuationToken = ReadString(obj, "continuationToken") is { } token ? ResponseContinuationToken.FromBytes(Convert.FromBase64String(token)) : null,
            ModelId = ReadString(obj, "modelId"),
            FinishReason = ReadString(obj, "finishReason") is { } finish ? new ChatFinishReason(finish) : null
        };
    }

    public static JsonNode CaptureUpdate(ChatResponseUpdate update) => new JsonObject
    {
        ["role"] = update.Role?.Value,
        ["authorName"] = update.AuthorName,
        ["contents"] = CaptureContents(update.Contents),
        ["messageId"] = update.MessageId,
        ["responseId"] = update.ResponseId,
        ["conversationId"] = update.ConversationId,
        ["continuationToken"] = update.ContinuationToken is null ? null : Convert.ToBase64String(update.ContinuationToken.ToBytes().Span),
        ["modelId"] = update.ModelId,
        ["finishReason"] = update.FinishReason?.Value
    };

    public static ChatResponseUpdate RestoreUpdate(JsonNode node)
    {
        var obj = node.AsObject();
        return new ChatResponseUpdate
        {
            Role = ReadString(obj, "role") is { } role ? new ChatRole(role) : null,
            AuthorName = ReadString(obj, "authorName"),
            Contents = ReadContents(obj["contents"]),
            MessageId = ReadString(obj, "messageId"),
            ResponseId = ReadString(obj, "responseId"),
            ConversationId = ReadString(obj, "conversationId"),
            ContinuationToken = ReadString(obj, "continuationToken") is { } token ? ResponseContinuationToken.FromBytes(Convert.FromBase64String(token)) : null,
            ModelId = ReadString(obj, "modelId"),
            FinishReason = ReadString(obj, "finishReason") is { } reason ? new ChatFinishReason(reason) : null
        };
    }

    private static JsonArray CaptureMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new JsonArray();
        foreach (var m in messages)
            result.Add(new JsonObject
            {
                ["role"] = m.Role.Value,
                ["authorName"] = m.AuthorName,
                ["messageId"] = m.MessageId,
                ["contents"] = CaptureContents(m.Contents)
            });
        return result;
    }

    private static List<ChatMessage> ReadMessages(JsonNode? node)
    {
        return (node?.AsArray() ?? throw new InvalidDataException("Missing messages."))
            .Select(n =>
            {
                var obj = n!.AsObject();
                return new ChatMessage(new ChatRole(ReadString(obj, "role")!), ReadContents(obj["contents"]))
                {
                    AuthorName = ReadString(obj, "authorName"),
                    MessageId = ReadString(obj, "messageId")
                };
            }).ToList();
    }

    private static JsonArray CaptureContents(IEnumerable<AIContent> contents)
    {
        var array = new JsonArray();
        foreach (var content in contents)
        {
            JsonObject obj = content switch
            {
                TextContent text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                FunctionCallContent call when call.Exception is null => new JsonObject
                {
                    ["type"] = "call", ["callId"] = call.CallId, ["name"] = call.Name,
                    ["arguments"] = SafeJson(call.Arguments), ["informationalOnly"] = call.InformationalOnly
                },
                FunctionResultContent result => new JsonObject
                {
                    ["type"] = "result", ["callId"] = result.CallId,
                    ["result"] = SafeJson(result.Result), ["isError"] = result.Exception is not null
                },
                UsageContent => new JsonObject { ["type"] = "usage" }, // usage statistics are deliberately ignored
                _ => throw new NotSupportedException($"Unsupported cassette AIContent: {content.GetType().FullName}")
            };
            array.Add(obj);
        }
        return array;
    }

    private static IList<AIContent> ReadContents(JsonNode? node)
    {
        var result = new List<AIContent>();
        foreach (var item in node?.AsArray() ?? throw new InvalidDataException("Missing contents."))
        {
            var obj = item!.AsObject();
            if (ReadString(obj, "type") == "usage") continue;
            result.Add(ReadString(obj, "type") switch
            {
                "text" => new TextContent(ReadString(obj, "text") ?? ""),
                "call" => new FunctionCallContent(ReadString(obj, "callId")!, ReadString(obj, "name")!,
                    obj["arguments"] is null ? null : ReadDictionary(obj["arguments"]!))
                {
                    InformationalOnly = obj["informationalOnly"]?.GetValue<bool>() ?? false
                },
                "result" => new FunctionResultContent(ReadString(obj, "callId")!, ReadValue(obj["result"])),
                "usage" => null!, // no behavior depends on token usage
                _ => throw new InvalidDataException("Unsupported cassette AIContent type.")
            });
        }
        return result;
    }

    private static IDictionary<string, object?> ReadDictionary(JsonNode node) =>
        node.AsObject().ToDictionary(kvp => kvp.Key, kvp => ReadValue(kvp.Value));

    private static object? ReadValue(JsonNode? node) =>
        node is null ? null : JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());

    private static JsonNode? SafeJson(object? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.SerializeToNode(value, value.GetType()); }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            throw new NotSupportedException($"Unsupported cassette JSON value: {value.GetType().FullName}", ex);
        }
    }

    private static JsonNode? CaptureOptions(ChatOptions? options)
    {
        if (options is null) return null;
        if (options.RawRepresentationFactory is not null)
            throw new NotSupportedException("Cassette cannot capture RawRepresentationFactory.");
        return new JsonObject
        {
            ["modelId"] = options.ModelId,
            ["instructions"] = options.Instructions,
            ["conversationId"] = options.ConversationId,
            ["continuationToken"] = options.ContinuationToken is null ? null : Convert.ToBase64String(options.ContinuationToken.ToBytes().Span),
            ["temperature"] = SafeJson(options.Temperature),
            ["topP"] = SafeJson(options.TopP),
            ["topK"] = SafeJson(options.TopK),
            ["maxOutputTokens"] = SafeJson(options.MaxOutputTokens),
            ["frequencyPenalty"] = SafeJson(options.FrequencyPenalty),
            ["presencePenalty"] = SafeJson(options.PresencePenalty),
            ["seed"] = SafeJson(options.Seed),
            ["allowMultipleToolCalls"] = SafeJson(options.AllowMultipleToolCalls),
            ["allowBackgroundResponses"] = SafeJson(options.AllowBackgroundResponses),
            ["toolMode"] = options.ToolMode is null ? null : JsonSerializer.SerializeToNode<ChatToolMode>(options.ToolMode),
            ["reasoning"] = SafeJson(options.Reasoning),
            ["responseFormat"] = options.ResponseFormat is null ? null : JsonSerializer.SerializeToNode<ChatResponseFormat>(options.ResponseFormat),
            ["stopSequences"] = SafeJson(options.StopSequences),
            ["additionalProperties"] = SafeJson(options.AdditionalProperties)
        };
    }

    private static JsonArray CaptureTools(IEnumerable<AITool>? tools)
    {
        var array = new JsonArray();
        if (tools is null) return array;
        foreach (var tool in tools)
        {
            if (tool is not AIFunctionDeclaration function)
                throw new NotSupportedException($"Unsupported cassette tool: {tool.GetType().FullName}");
            array.Add(new JsonObject
            {
                ["name"] = function.Name,
                ["description"] = function.Description,
                ["schema"] = SafeJson(function.JsonSchema)
            });
        }
        return array;
    }

    private void Normalize(JsonNode node) => RewritePaths(node, restore: false);
    private void Restore(JsonNode node) => RewritePaths(node, restore: true);

    private void RewritePaths(JsonNode node, bool restore)
    {
        if (string.IsNullOrEmpty(_root)) return;
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    obj[key] = ChangePath(text, restore);
                else if (value is not null) RewritePaths(value, restore);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    array[i] = ChangePath(text, restore);
                else if (array[i] is { } child) RewritePaths(child, restore);
            }
        }
    }

    private string ChangePath(string text, bool restore)
    {
        if (restore) return text.Replace(RootToken, _root!, StringComparison.Ordinal);
        var normalized = text.Replace('\\', '/');
        var root = _root!;
        if (!normalized.Contains(root, StringComparison.OrdinalIgnoreCase)) return text;
        var start = 0;
        while ((start = normalized.IndexOf(root, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = start + root.Length;
            if ((start == 0 || (!char.IsLetterOrDigit(normalized[start - 1]) && normalized[start - 1] != '_')) &&
                (end == normalized.Length || normalized[end] == '/' || normalized[end] is ' ' or '\\t' or '\\r' or '\\n' or '\\"' or '\\\'' or ')' or ']' or '}' or ',' or ';'))
            {
                normalized = normalized[..start] + RootToken + normalized[end..];
                start += RootToken.Length;
            }
            else start = end;
        }
        return normalized;
    }

    private static string? ReadString(JsonObject obj, string property) => obj[property]?.GetValue<string>();

    private static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is JsonObject left && actual is JsonObject right)
        {
            foreach (var key in left.Select(x => x.Key).Union(right.Select(x => x.Key)).OrderBy(x => x))
            {
                if (!left.ContainsKey(key) || !right.ContainsKey(key))
                    return Diagnostic(path + "." + key, left.ContainsKey(key) ? left[key] : null,
                        right.ContainsKey(key) ? right[key] : null,
                        left.ContainsKey(key) ? null : "<missing>", right.ContainsKey(key) ? null : "<missing>");
                var difference = FirstDifference(left[key], right[key], path + "." + key);
                if (difference is not null) return difference;
            }
            return null;
        }
        if (expected is JsonArray la && actual is JsonArray ra)
        {
            if (la.Count != ra.Count) return Diagnostic(path + ".length", JsonValue.Create(la.Count), JsonValue.Create(ra.Count));
            for (var i = 0; i < la.Count; i++)
            {
                var difference = FirstDifference(la[i], ra[i], $"{path}[{i}]");
                if (difference is not null) return difference;
            }
            return null;
        }
        return JsonNode.DeepEquals(expected, actual) ? null : Diagnostic(path, expected, actual);
    }

    private static string Diagnostic(string path, JsonNode? expected, JsonNode? actual,
        string? explicitExpected = null, string? explicitActual = null)
    {
        static string Clip(string text) => text.Length > 180 ? text[..180] + "…" : text;
        return $"Path: {path}\\nExpected: {Clip(explicitExpected ?? expected?.ToJsonString() ?? "null")}\\nActual: {Clip(explicitActual ?? actual?.ToJsonString() ?? "null")}";
    }
}

/// <summary>Uses the normal factory only in Record. A scenario is allowed one client.</summary>
internal sealed class CassetteChatClientFactory(ILlmChatClientFactory actual, LlmCassette cassette) : ILlmChatClientFactory
{
    public async Task<IChatClient> CreateAsync(ServerModel model, CancellationToken cancellationToken = default)
    {
        cassette.ClaimClient();
        if (!cassette.IsRecording) return new ReplayChatClient(cassette);
        return new RecordingChatClient(await actual.CreateAsync(model, cancellationToken), cassette);
    }
}

internal sealed class RecordingChatClient(IChatClient inner, LlmCassette cassette) : IChatClient
{
    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);
    public void Dispose() => inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var active = cassette.Enter();
        try
        {
            var request = cassette.CaptureRequest(messages, options);
            var response = await inner.GetResponseAsync(messages, options, cancellationToken);
            cassette.Add("response", request, LlmCassette.CaptureResponse(response));
            return response;
        }
        catch { cassette.Fail(); throw; }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var active = cassette.Enter();
        var finished = false;
        try
        {
            var request = cassette.CaptureRequest(messages, options);
            var updates = new JsonArray();
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                updates.Add(LlmCassette.CaptureUpdate(update));
                yield return update;
            }
            cassette.Add("stream", request, new JsonObject { ["updates"] = updates });
            finished = true;
        }
        finally { if (!finished) cassette.Fail(); }
    }
}

internal sealed class ReplayChatClient(LlmCassette cassette) : IChatClient
{
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("cassette", null, "cassette-model") : null;
    public void Dispose() { }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var active = cassette.Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = cassette.Next("response", cassette.CaptureRequest(messages, options));
            return Task.FromResult(LlmCassette.RestoreResponse(response));
        }
        catch { cassette.Fail(); throw; }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var active = cassette.Enter();
        var finished = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = cassette.Next("stream", cassette.CaptureRequest(messages, options));
            foreach (var update in response["updates"]!.AsArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return LlmCassette.RestoreUpdate(update!);
            }
            finished = true;
        }
        finally { if (!finished) cassette.Fail(); }
    }
}
