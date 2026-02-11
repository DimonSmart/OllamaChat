using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.SemanticKernel;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticToolInvocationPolicyFilter(
    AgenticToolInvocationPolicyOptions policyOptions,
    ILogger<AgenticToolInvocationPolicyFilter> logger) : IFunctionInvocationFilter
{
    private static readonly JsonSerializerOptions StableJson = new()
    {
        WriteIndented = false
    };

    private readonly ConcurrentQueue<FunctionCallRecord> _records = [];
    private long _sequence;

    public IReadOnlyList<FunctionCallRecord> Records => _records.ToList();

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        ValidateArgumentsAgainstSchema(context);

        long invocationId = Interlocked.Increment(ref _sequence);
        string server = context.Function.PluginName ?? "McpServer";
        string function = context.Function.Name;
        string requestPayload = BuildCanonicalRequest(context);
        int maxAttempts = Math.Max(1, policyOptions.MaxRetries + 1);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, policyOptions.TimeoutSeconds));

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var invokeTask = next(context);
                await invokeTask.WaitAsync(timeout, context.CancellationToken);

                stopwatch.Stop();
                string responsePayload = BuildCanonicalResponse(context.Result?.GetValue<object>());
                string diagnostic = $"status=ok;attempt={attempt};durationMs={stopwatch.ElapsedMilliseconds};response={responsePayload}";

                _records.Enqueue(new FunctionCallRecord(server, function, requestPayload, diagnostic));
                logger.LogInformation(
                    "Agentic tool invocation success | id={InvocationId} | plugin={Plugin} | function={Function} | attempt={Attempt} | durationMs={DurationMs} | request={Request} | response={Response}",
                    invocationId,
                    server,
                    function,
                    attempt,
                    stopwatch.ElapsedMilliseconds,
                    requestPayload,
                    responsePayload);

                return;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < maxAttempts && !context.CancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                logger.LogWarning(
                    ex,
                    "Agentic tool invocation retry | id={InvocationId} | plugin={Plugin} | function={Function} | attempt={Attempt}/{MaxAttempts} | durationMs={DurationMs} | request={Request}",
                    invocationId,
                    server,
                    function,
                    attempt,
                    maxAttempts,
                    stopwatch.ElapsedMilliseconds,
                    requestPayload);

                if (policyOptions.RetryDelayMs > 0)
                {
                    await Task.Delay(policyOptions.RetryDelayMs, context.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                string responsePayload = BuildErrorPayload(ex);
                string diagnostic = $"status=error;attempt={attempt};durationMs={stopwatch.ElapsedMilliseconds};response={responsePayload}";
                _records.Enqueue(new FunctionCallRecord(server, function, requestPayload, diagnostic));

                logger.LogError(
                    ex,
                    "Agentic tool invocation failed | id={InvocationId} | plugin={Plugin} | function={Function} | attempt={Attempt} | durationMs={DurationMs} | request={Request} | error={Error}",
                    invocationId,
                    server,
                    function,
                    attempt,
                    stopwatch.ElapsedMilliseconds,
                    requestPayload,
                    responsePayload);
                throw;
            }
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        return exception switch
        {
            TimeoutException => true,
            HttpRequestException => true,
            TaskCanceledException => true,
            OperationCanceledException => true,
            _ => false
        };
    }

    private static void ValidateArgumentsAgainstSchema(FunctionInvocationContext context)
    {
        var parameters = context.Function.Metadata.Parameters;
        if (parameters is null || parameters.Count == 0)
        {
            if (context.Arguments.Count > 0)
            {
                throw new InvalidOperationException($"Function '{context.Function.Name}' does not accept arguments.");
            }
            return;
        }

        var expectedNames = parameters
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknownArguments = context.Arguments
            .Select(a => a.Key)
            .Where(k => !expectedNames.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unknownArguments.Count > 0)
        {
            throw new InvalidOperationException(
                $"Function '{context.Function.Name}' received unsupported arguments: {string.Join(", ", unknownArguments)}.");
        }

        var missingRequired = parameters
            .Where(p => p.IsRequired)
            .Where(p => !context.Arguments.TryGetValue(p.Name, out var value) || IsMissingValue(value))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingRequired.Count > 0)
        {
            throw new InvalidOperationException(
                $"Function '{context.Function.Name}' is missing required arguments: {string.Join(", ", missingRequired)}.");
        }

        foreach (var parameter in parameters)
        {
            if (!context.Arguments.TryGetValue(parameter.Name, out var value) || value is null)
            {
                continue;
            }

            if (parameter.ParameterType is null || parameter.ParameterType == typeof(object))
            {
                continue;
            }

            ValidateParameterType(context.Function.Name, parameter.Name, parameter.ParameterType, value);
        }
    }

    private static bool IsMissingValue(object? value)
    {
        if (value is null)
            return true;
        if (value is string s && string.IsNullOrWhiteSpace(s))
            return true;
        if (value is JsonElement element &&
            (element.ValueKind == JsonValueKind.Null ||
             (element.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(element.GetString()))))
            return true;
        return false;
    }

    private static void ValidateParameterType(string functionName, string parameterName, Type expectedType, object value)
    {
        if (expectedType.IsInstanceOfType(value))
        {
            return;
        }

        try
        {
            if (value is JsonElement jsonElement)
            {
                _ = jsonElement.Deserialize(expectedType, StableJson);
                return;
            }

            if (expectedType.IsEnum && value is string enumValue)
            {
                _ = Enum.Parse(expectedType, enumValue, ignoreCase: true);
                return;
            }

            _ = Convert.ChangeType(value, expectedType, CultureInfo.InvariantCulture);
        }
        catch
        {
            throw new InvalidOperationException(
                $"Function '{functionName}' argument '{parameterName}' does not match expected type '{expectedType.Name}'.");
        }
    }

    private static string BuildCanonicalRequest(FunctionInvocationContext context)
    {
        var normalized = new JsonObject();
        foreach (var argument in context.Arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            normalized[argument.Key] = NormalizeNode(argument.Value);
        }

        return SerializeStable(normalized);
    }

    private static string BuildCanonicalResponse(object? value)
    {
        var node = NormalizeNode(value);
        return SerializeStable(node);
    }

    private static string BuildErrorPayload(Exception exception)
    {
        var node = new JsonObject
        {
            ["errorType"] = exception.GetType().Name,
            ["message"] = exception.Message
        };
        return SerializeStable(node);
    }

    private static JsonNode? NormalizeNode(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonNode jsonNode)
            return SortJsonNode(jsonNode.DeepClone());

        if (value is JsonElement jsonElement)
            return SortJsonNode(JsonNode.Parse(jsonElement.GetRawText()));

        JsonNode? serialized = JsonSerializer.SerializeToNode(value, StableJson);
        return SortJsonNode(serialized);
    }

    private static JsonNode? SortJsonNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sorted[kv.Key] = SortJsonNode(kv.Value);
            }
            return sorted;
        }

        if (node is JsonArray arr)
        {
            var sortedArray = new JsonArray();
            foreach (var item in arr)
            {
                sortedArray.Add(SortJsonNode(item));
            }
            return sortedArray;
        }

        return node;
    }

    private static string SerializeStable(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        return node.ToJsonString(StableJson);
    }
}
