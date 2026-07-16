using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
#pragma warning restore MAAI001
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record AgenticRuntimeAgentBuildResult(
    AIAgent Agent,
    LlmServerConfig Server,
    AgenticToolSet ToolSet,
    bool SupportsFunctionCalling);

public sealed class AgenticRuntimeAgentFactory(
    ILlmServerConfigService llmServerConfigService,
    ILlmChatClientFactory llmChatClientFactory,
    IModelCapabilityService modelCapabilityService,
    IAppToolCatalog appToolCatalog,
    IMcpUserInteractionService mcpUserInteractionService,
    KernelService kernelService,
    IOptions<ChatEngineOptions> chatEngineOptions,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    ILogger<AgenticRuntimeAgentFactory> logger)
{
    private const int MaxLoggedPayloadLength = 4000;

    private static readonly JsonSerializerOptions ToolResultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AgenticToolInvocationPolicyOptions _toolPolicy =
        NormalizeToolPolicy(chatEngineOptions.Value.ToolPolicy);

    internal async Task<AgenticRuntimeAgentBuildResult> CreateAsync(
        AgentRunRequest request,
        List<FunctionCallRecord>? functionCalls = null,
        bool requireFunctionCalling = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var server = await llmServerConfigService.GetByIdAsync(request.ResolvedModel.ServerId);
        if (server is null)
        {
            throw new InvalidOperationException(
                $"Configured LLM server '{request.ResolvedModel.ServerId}' was not found.");
        }

        var chatClient = await llmChatClientFactory.CreateAsync(request.ResolvedModel, cancellationToken);
        bool supportsFunctions = await modelCapabilityService.SupportsFunctionCallingAsync(
            request.ResolvedModel,
            cancellationToken);

        var toolRequestContext = BuildToolRequestContext(request);
        var availableTools = supportsFunctions
            ? await appToolCatalog.ListToolsAsync(toolRequestContext, cancellationToken)
            : [];
        var requestedFunctions = await ResolveRequestedFunctionNamesAsync(request, availableTools, cancellationToken);

        if (!supportsFunctions && requestedFunctions.Count > 0)
        {
            if (requireFunctionCalling)
            {
                throw new InvalidOperationException(
                    $"Model '{request.ResolvedModel.ModelName}' does not support function calling required by workflow agent '{request.Agent.AgentName}'.");
            }

            logger.LogInformation(
                "Model {ModelName} for agent {AgentName} does not support function calling. Skipping {FunctionCount} configured tools.",
                request.ResolvedModel.ModelName,
                request.Agent.AgentName,
                requestedFunctions.Count);
        }

        var toolSet = supportsFunctions
            ? AgenticToolSetBuilder.Build(requestedFunctions, availableTools)
            : AgenticToolSet.Empty;

        if (requestedFunctions.Count > 0 && !toolSet.HasTools)
        {
            logger.LogWarning(
                "No MCP tools matched the configured function set for agent {AgentName}. Requested: [{RequestedFunctions}]",
                request.Agent.AgentName,
                string.Join(", ", requestedFunctions));
        }

        var runtimeAgent = CreateRuntimeAgent(chatClient, server, request, toolSet, functionCalls);
        return new AgenticRuntimeAgentBuildResult(
            runtimeAgent,
            server,
            toolSet,
            supportsFunctions);
    }

    private AIAgent CreateRuntimeAgent(
        IChatClient chatClient,
        LlmServerConfig server,
        AgentRunRequest request,
        AgenticToolSet toolSet,
        List<FunctionCallRecord>? functionCalls)
    {
        var historyCompaction = AgentHistoryCompactionFactory.Create(
            request.Agent,
            toolSet,
            loggerFactory,
            logger);

        var agentOptions = new ChatClientAgentOptions
        {
            Id = string.IsNullOrWhiteSpace(request.Agent.AgentId) ? null : request.Agent.AgentId.Trim(),
            Name = string.IsNullOrWhiteSpace(request.Agent.AgentName) ? null : request.Agent.AgentName.Trim(),
            ChatOptions = new ChatOptions
            {
                Instructions = BuildInstructions(request.Agent, historyCompaction),
                Tools = toolSet.Tools.ToList()
            },
            AIContextProviders = historyCompaction is null ? null : [historyCompaction.Provider],
            UseProvidedChatClientAsIs = false
        };

        var baseAgent = new ChatClientAgent(
            chatClient,
            agentOptions,
            loggerFactory,
            serviceProvider);

        if (!toolSet.HasTools)
        {
            return baseAgent;
        }

        return baseAgent
            .AsBuilder()
            .Use(async (_, context, next, cancellationToken) =>
            {
                if (!toolSet.MetadataByName.TryGetValue(context.Function.Name, out var tool))
                {
                    return await next(context, cancellationToken);
                }

                var requestPayload = SerializeArguments(context.Arguments);
                var requestForLog = FormatForLog(requestPayload, MaxLoggedPayloadLength);
                int maxAttempts = Math.Max(1, _toolPolicy.MaxRetries + 1);
                Exception? lastException = null;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    var startedAt = DateTime.UtcNow;

                    try
                    {
                        if (attempt == 1)
                        {
                            logger.LogInformation(
                                "Calling MCP tool {Server}:{ToolName} via framework tool {RegisteredName}. Arguments: {Arguments}",
                                tool.ServerName,
                                tool.ToolName,
                                tool.RegisteredName,
                                requestForLog);
                        }
                        else
                        {
                            logger.LogInformation(
                                "Retrying MCP tool {Server}:{ToolName} via framework tool {RegisteredName} (attempt {Attempt}/{MaxAttempts}).",
                                tool.ServerName,
                                tool.ToolName,
                                tool.RegisteredName,
                                attempt,
                                maxAttempts);
                        }

                        var timeoutSeconds = tool.MayRequireUserInput
                            ? Math.Max(_toolPolicy.TimeoutSeconds, _toolPolicy.InteractiveTimeoutSeconds)
                            : _toolPolicy.TimeoutSeconds;

                        using var timeoutCts = timeoutSeconds > 0
                            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                            : null;
                        if (timeoutSeconds > 0)
                        {
                            timeoutCts!.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                        }

                        var executionToken = timeoutCts?.Token ?? cancellationToken;
                        using var interactionScope = mcpUserInteractionService.BeginInteractionScope(McpInteractionScope.Chat);
                        var result = await next(context, executionToken);
                        var responsePayload = AgenticToolUtility.SerializeForToolTransport(result, ToolResultJsonOptions);
                        var durationMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);

                        logger.LogInformation(
                            "MCP tool {Server}:{ToolName} completed via framework tool {RegisteredName} (attempt {Attempt}/{MaxAttempts}, {DurationMs} ms). Response: {Response}",
                            tool.ServerName,
                            tool.ToolName,
                            tool.RegisteredName,
                            attempt,
                            maxAttempts,
                            durationMs,
                            FormatForLog(responsePayload, MaxLoggedPayloadLength));

                        functionCalls?.Add(new FunctionCallRecord(
                            tool.ServerName,
                            tool.ToolName,
                            requestPayload,
                            $"status=ok;attempt={attempt};durationMs={durationMs};response={responsePayload}"));

                        return result;
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        lastException = ex;
                        var durationMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
                        logger.LogWarning(
                            "Tool {Server}:{ToolName} timed out on attempt {Attempt}/{MaxAttempts} after {DurationMs} ms.",
                            tool.ServerName,
                            tool.ToolName,
                            attempt,
                            maxAttempts,
                            durationMs);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        var durationMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
                        logger.LogWarning(
                            ex,
                            "Tool {Server}:{ToolName} failed on attempt {Attempt}/{MaxAttempts} after {DurationMs} ms.",
                            tool.ServerName,
                            tool.ToolName,
                            attempt,
                            maxAttempts,
                            durationMs);
                    }

                    if (attempt < maxAttempts && _toolPolicy.RetryDelayMs > 0)
                    {
                        await Task.Delay(_toolPolicy.RetryDelayMs, cancellationToken);
                    }
                }

                var finalMessage = lastException?.Message ?? "Unknown tool execution failure.";
                var errorPayload = new Dictionary<string, object?>
                {
                    ["error"] = finalMessage
                };
                var serializedPayload = JsonSerializer.Serialize(errorPayload, ToolResultJsonOptions);

                logger.LogError(
                    lastException,
                    "Tool execution failed for {Server}:{ToolName} (framework tool {RegisteredName}) after {MaxAttempts} attempts. Arguments: {Arguments}",
                    tool.ServerName,
                    tool.ToolName,
                    tool.RegisteredName,
                    maxAttempts,
                    requestForLog);

                functionCalls?.Add(new FunctionCallRecord(
                    tool.ServerName,
                    tool.ToolName,
                    requestPayload,
                    $"status=error;attempt={maxAttempts};response={serializedPayload}"));

                return errorPayload;
            })
            .Build();
    }

    private static string? BuildInstructions(
        AgentExecutionSpec agent,
        AgentHistoryCompactionAttachment? historyCompaction)
    {
        var content = agent.Content?.Trim();
        if (historyCompaction is null)
        {
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return historyCompaction.InstructionNote;
        }

        return $"{content}\n\n{historyCompaction.InstructionNote}";
    }

    private async Task<IReadOnlyList<string>> ResolveRequestedFunctionNamesAsync(
        AgentRunRequest request,
        IReadOnlyCollection<AppToolDescriptor> availableTools,
        CancellationToken cancellationToken)
    {
        HashSet<string> requested = new(StringComparer.OrdinalIgnoreCase);

        foreach (var function in request.Configuration.Functions)
        {
            if (string.IsNullOrWhiteSpace(function))
            {
                continue;
            }

            requested.Add(function.Trim());
        }

        foreach (var function in McpBindingToolSelectionResolver.ResolveQualifiedToolNames(
                     request.Agent.McpServerBindings,
                     availableTools))
        {
            requested.Add(function);
        }

        try
        {
            var fromAgentSettings = await kernelService.GetFunctionsToRegisterAsync(
                request.Agent.FunctionSettings,
                request.UserMessage,
                availableTools,
                cancellationToken);

            foreach (var function in fromAgentSettings)
            {
                if (string.IsNullOrWhiteSpace(function))
                {
                    continue;
                }

                requested.Add(function.Trim());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve function selection for agent {AgentName}. Falling back to explicit configuration only.",
                request.Agent.AgentName);
        }

        return requested.ToList();
    }

    private static McpClientRequestContext BuildToolRequestContext(AgentRunRequest request)
    {
        var mergedBindings = McpServerSessionBindingMerger.Merge(
            request.Agent.McpServerBindings,
            request.Configuration.McpServerBindings);

        return mergedBindings.Count == 0
            ? McpClientRequestContext.Empty
            : new McpClientRequestContext(mergedBindings);
    }

    private static AgenticToolInvocationPolicyOptions NormalizeToolPolicy(AgenticToolInvocationPolicyOptions? policy)
    {
        policy ??= new AgenticToolInvocationPolicyOptions();

        return new AgenticToolInvocationPolicyOptions
        {
            TimeoutSeconds = Math.Max(0, policy.TimeoutSeconds),
            InteractiveTimeoutSeconds = Math.Max(0, policy.InteractiveTimeoutSeconds),
            MaxRetries = Math.Max(0, policy.MaxRetries),
            RetryDelayMs = Math.Max(0, policy.RetryDelayMs)
        };
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
