using ChatClient.Api.Services;
using ChatClient.Application.Services.Agentic;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record AgenticRegisteredTool(
    string RegisteredName,
    string ServerName,
    string ToolName,
    string Source,
    string? BindingName,
    bool MayRequireUserInput,
    AITool Tool);

internal sealed record AgenticToolSet(
    IReadOnlyList<AITool> Tools,
    IReadOnlyDictionary<string, AgenticRegisteredTool> MetadataByName)
{
    public static AgenticToolSet Empty { get; } =
        new([], new Dictionary<string, AgenticRegisteredTool>(StringComparer.OrdinalIgnoreCase));

    public bool HasTools => Tools.Count > 0;
}

internal static class AgenticToolSetBuilder
{
    private static readonly JsonElement EmptyObjectSchema = CreateEmptyObjectSchema();

    public static AgenticToolSet Build(
        IReadOnlyCollection<string> requestedFunctions,
        IReadOnlyCollection<AppToolDescriptor> availableTools,
        AgenticToolInvocationPolicyOptions policy,
        IMcpUserInteractionService mcpUserInteractionService,
        ILogger logger)
    {
        List<AgenticToolSpec> specs = [];

        if (requestedFunctions.Count > 0)
        {
            var exactRequested = new HashSet<string>(
                requestedFunctions
                    .Where(ContainsQualifier)
                    .Select(static value => value.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var shortRequested = new HashSet<string>(
                requestedFunctions
                    .Where(static value => !ContainsQualifier(value))
                    .Select(static value => value.Trim()),
                StringComparer.OrdinalIgnoreCase);

            foreach (var tool in availableTools)
            {
                bool isSelected = exactRequested.Contains(tool.QualifiedName) ||
                                  shortRequested.Contains(tool.ToolName);
                if (!isSelected)
                {
                    continue;
                }

                specs.Add(AgenticToolSpec.FromAppTool(tool));
            }
        }

        if (specs.Count == 0)
        {
            return AgenticToolSet.Empty;
        }

        var registeredNames = AssignRegisteredNames(specs);
        List<AITool> tools = [];
        Dictionary<string, AgenticRegisteredTool> metadataByName = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var registeredName = registeredNames[i];
            var description = BuildRegisteredDescription(spec, registeredName);
            var tool = BuildTool(spec, registeredName, description, policy, mcpUserInteractionService, logger);

            tools.Add(tool);
            metadataByName[registeredName] = new AgenticRegisteredTool(
                registeredName,
                spec.ServerName,
                spec.ToolName,
                spec.Source,
                spec.BindingName,
                spec.MayRequireUserInput,
                tool);
        }

        return new AgenticToolSet(tools, metadataByName);
    }

    private static AITool BuildTool(
        AgenticToolSpec spec,
        string registeredName,
        string description,
        AgenticToolInvocationPolicyOptions policy,
        IMcpUserInteractionService mcpUserInteractionService,
        ILogger logger)
    {
        var schema = NormalizeInputSchema(spec.InputSchema);
        var innerFunction = AIFunctionFactory.Create(
            (AIFunctionArguments arguments, CancellationToken cancellationToken) =>
                spec.ExecuteAsync(ToDictionary(arguments), cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = registeredName,
                Description = description,
                ExcludeResultSchema = spec.OutputSchema is null
            });

        var configured = new ConfiguredAgenticFunction(
            innerFunction,
            registeredName,
            description,
            schema,
            spec.OutputSchema);

        return new PolicyAgenticFunction(
            configured,
            spec.ServerName,
            spec.ToolName,
            spec.MayRequireUserInput,
            policy,
            mcpUserInteractionService,
            logger);
    }

    private static Dictionary<string, object?> ToDictionary(AIFunctionArguments arguments)
    {
        Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in arguments)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static IReadOnlyList<string> AssignRegisteredNames(IReadOnlyList<AgenticToolSpec> specs)
    {
        Dictionary<string, int> countsByToolName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            countsByToolName[spec.ToolName] = countsByToolName.GetValueOrDefault(spec.ToolName) + 1;
        }

        HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
        List<string> names = [];

        foreach (var spec in specs)
        {
            if (countsByToolName[spec.ToolName] == 1 && usedNames.Add(spec.ToolName))
            {
                names.Add(spec.ToolName);
                continue;
            }

            names.Add(CreateDisambiguatedToolName(spec.SourceLabel, spec.ToolName, usedNames));
        }

        return names;
    }

    private static string BuildRegisteredDescription(AgenticToolSpec spec, string registeredName)
    {
        var description = string.IsNullOrWhiteSpace(spec.Description)
            ? $"Tool from {spec.ServerName}."
            : spec.Description.Trim();

        if (string.Equals(registeredName, spec.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            return description;
        }

        return $"{description}\n\nSource server: {spec.ServerName}. Original tool name: {spec.ToolName}.";
    }

    private static bool ContainsQualifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(':', StringComparison.Ordinal);

    private static string CreateDisambiguatedToolName(
        string sourceLabel,
        string toolName,
        HashSet<string> usedNames)
    {
        const int maxLength = 64;
        var baseName = $"{SanitizeToolNamePart(sourceLabel)}__{SanitizeToolNamePart(toolName)}";
        if (baseName.Length > maxLength)
        {
            baseName = baseName[..maxLength];
        }

        var candidate = baseName;
        var suffix = 1;
        while (!usedNames.Add(candidate))
        {
            var suffixText = $"_{suffix++}";
            var prefixLength = Math.Max(1, maxLength - suffixText.Length);
            candidate = $"{baseName[..Math.Min(baseName.Length, prefixLength)]}{suffixText}";
        }

        return candidate;
    }

    private static string SanitizeToolNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "tool";
        }

        StringBuilder builder = new(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return builder.Length == 0 ? "tool" : builder.ToString();
    }

    private static JsonElement CreateEmptyObjectSchema()
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return document.RootElement.Clone();
    }

    private static JsonElement NormalizeInputSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
            ? schema.Clone()
            : EmptyObjectSchema.Clone();

    private static JsonElement? NormalizeOutputSchema(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } value
            ? value.Clone()
            : null;

    private sealed record AgenticToolSpec(
        string ServerName,
        string SourceLabel,
        string ToolName,
        string Source,
        string? BindingName,
        string Description,
        JsonElement InputSchema,
        JsonElement? OutputSchema,
        bool MayRequireUserInput,
        Func<Dictionary<string, object?>, CancellationToken, Task<object>> ExecuteAsync)
    {
        public static AgenticToolSpec FromAppTool(AppToolDescriptor tool)
        {
            var inputSchema = NormalizeInputSchema(tool.InputSchema);
            var outputSchema = NormalizeOutputSchema(tool.OutputSchema);
            var sourceLabel = string.IsNullOrWhiteSpace(tool.BindingDisplayName)
                ? tool.BaseServerName ?? tool.ServerName
                : tool.BindingDisplayName.Trim();

            return new AgenticToolSpec(
                tool.ServerName,
                sourceLabel,
                tool.ToolName,
                tool.BindingId is null ? "application" : "mcp",
                tool.BindingDisplayName,
                tool.Description,
                inputSchema,
                outputSchema,
                tool.MayRequireUserInput,
                tool.ExecuteAsync);
        }
    }
}

internal sealed class ConfiguredAgenticFunction(
    AIFunction innerFunction,
    string name,
    string description,
    JsonElement jsonSchema,
    JsonElement? returnJsonSchema) : DelegatingAIFunction(innerFunction)
{
    private readonly JsonElement _jsonSchema = jsonSchema.Clone();
    private readonly JsonElement? _returnJsonSchema = returnJsonSchema?.Clone();

    public override string Name => name;

    public override string Description => description;

    public override JsonElement JsonSchema => _jsonSchema;

    public override JsonElement? ReturnJsonSchema => _returnJsonSchema;
}

internal sealed class PolicyAgenticFunction : DelegatingAIFunction
{
    private const int MaxLoggedPayloadLength = 4000;
    private readonly AIFunction _innerFunction;
    private readonly string _serverName;
    private readonly string _toolName;
    private readonly bool _mayRequireUserInput;
    private readonly AgenticToolInvocationPolicyOptions _policy;
    private readonly IMcpUserInteractionService _mcpUserInteractionService;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions ToolResultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PolicyAgenticFunction(
        AIFunction innerFunction,
        string serverName,
        string toolName,
        bool mayRequireUserInput,
        AgenticToolInvocationPolicyOptions policy,
        IMcpUserInteractionService mcpUserInteractionService,
        ILogger logger) : base(innerFunction)
    {
        _innerFunction = innerFunction;
        _serverName = serverName;
        _toolName = toolName;
        _mayRequireUserInput = mayRequireUserInput;
        _policy = policy;
        _mcpUserInteractionService = mcpUserInteractionService;
        _logger = logger;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = _mayRequireUserInput
            ? _policy.InteractiveTimeoutSeconds
            : _policy.TimeoutSeconds;
        var attempts = Math.Max(1, _policy.MaxRetries + 1);
        var serializedArguments = SerializeArguments(ToDictionary(arguments));

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var timeoutSource = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : new CancellationTokenSource();
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            using var interactionScope = _mcpUserInteractionService.BeginInteractionScope(McpInteractionScope.Chat);

            var startedAt = Stopwatch.GetTimestamp();
            _logger.LogInformation(
                "Tool {ToolName} from {ServerName} started. Attempt {Attempt}/{Attempts}. Args={Arguments}",
                _toolName,
                _serverName,
                attempt,
                attempts,
                FormatForLog(serializedArguments, MaxLoggedPayloadLength));

            try
            {
                var result = await _innerFunction.InvokeAsync(arguments, linkedSource.Token);
                var elapsed = Stopwatch.GetElapsedTime(startedAt);

                _logger.LogInformation(
                    "Tool {ToolName} from {ServerName} completed in {ElapsedMs} ms. Result={Result}",
                    _toolName,
                    _serverName,
                    elapsed.TotalMilliseconds,
                    FormatForLog(SerializeResult(result), MaxLoggedPayloadLength));

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Tool {ToolName} from {ServerName} canceled by caller.",
                    _toolName,
                    _serverName);
                throw;
            }
            catch (OperationCanceledException) when (timeoutSeconds > 0 && timeoutSource.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Tool {ToolName} from {ServerName} timed out after {TimeoutSeconds} seconds on attempt {Attempt}/{Attempts}.",
                    _toolName,
                    _serverName,
                    timeoutSeconds,
                    attempt,
                    attempts);

                if (attempt >= attempts)
                {
                    throw;
                }
            }
            catch (Exception ex) when (attempt < attempts)
            {
                _logger.LogWarning(
                    ex,
                    "Tool {ToolName} from {ServerName} failed on attempt {Attempt}/{Attempts}; retrying.",
                    _toolName,
                    _serverName,
                    attempt,
                    attempts);
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                _logger.LogError(
                    ex,
                    "Tool {ToolName} from {ServerName} failed after {ElapsedMs} ms.",
                    _toolName,
                    _serverName,
                    elapsed.TotalMilliseconds);
                throw;
            }

            if (_policy.RetryDelayMs > 0)
            {
                await Task.Delay(_policy.RetryDelayMs, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Tool '{_toolName}' failed without producing a result.");
    }

    private static Dictionary<string, object?> ToDictionary(AIFunctionArguments arguments)
    {
        Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in arguments)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static string SerializeArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        try
        {
            return JsonSerializer.Serialize(arguments, ToolResultJsonOptions);
        }
        catch
        {
            return "{}";
        }
    }

    private static string SerializeResult(object? result)
    {
        try
        {
            return JsonSerializer.Serialize(result, ToolResultJsonOptions);
        }
        catch
        {
            return result?.ToString() ?? "null";
        }
    }

    private static string FormatForLog(string? payload, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "<empty>";
        }

        var singleLine = payload.Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return $"{singleLine[..maxLength]}... (truncated, {singleLine.Length} chars)";
    }
}
