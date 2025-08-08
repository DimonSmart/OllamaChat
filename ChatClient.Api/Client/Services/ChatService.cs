using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp.Models.Exceptions;
#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService,
    IChatHistoryBuilder historyBuilder,
    ILogger<ChatService> logger) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private StreamingMessageManager _streamingManager = null!;
    private readonly Dictionary<string, StreamState> _activeStreams = new();
    private List<AgentDescription> _agentDescriptions = [];
    private readonly List<ChatCompletionAgent> _agents = [];
    private ReasonableRoundRobinGroupChatManager _groupChatManager = new(string.Empty, string.Empty);
    private GroupChatOrchestration? _chatOrchestration;
    private InProcessRuntime? _runtime;

    private sealed class StreamState
    {
        public StreamingAppChatMessage Message { get; }
        public int ApproximateTokenCount { get; set; }
        public DateTime StartTime { get; set; }
        public int FunctionCallStartIndex { get; set; }

        public StreamState(StreamingAppChatMessage message, int functionCallStartIndex)
        {
            Message = message;
            StartTime = DateTime.Now;
            FunctionCallStartIndex = functionCallStartIndex;
            ApproximateTokenCount = 0;
        }
    }
    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsLoading { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];
    public IReadOnlyList<AgentDescription> AgentDescriptions => _agentDescriptions;

    public void InitializeChat(IEnumerable<AgentDescription> initialAgents)
    {
        if (initialAgents is null)
        {
            throw new ArgumentNullException(nameof(initialAgents));
        }

        var agentsList = initialAgents.ToList();
        if (agentsList.Count == 0)
        {
            throw new ArgumentException("At least one agent must be selected.", nameof(initialAgents));
        }

        Messages.Clear();
        _activeStreams.Clear();
        _streamingManager = new StreamingMessageManager(MessageUpdated);
        _agentDescriptions = agentsList;
        _agents.Clear();

        foreach (var agent in _agentDescriptions)
        {
            var systemMessage = new AppChatMessage(agent.Content, DateTime.Now, ChatRole.System, string.Empty);
            Messages.Add(systemMessage);
        }

        _groupChatManager = new ReasonableRoundRobinGroupChatManager(string.Empty, string.Empty);
        _chatOrchestration = null;

        ChatInitialized?.Invoke();
    }

    public void ClearChat()
    {
        Messages.Clear();
        _agentDescriptions.Clear();
        _agents.Clear();
        _chatOrchestration = null;
        _runtime = null;
        _activeStreams.Clear();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();

        if (_streamingManager != null && _activeStreams.Any())
        {
            foreach (var state in _activeStreams.Values.ToList())
            {
                AppChatMessage canceledMessage = _streamingManager.CancelStreaming(state.Message);
                await ReplaceStreamingMessageWithFinal(state.Message, canceledMessage);
            }
            _activeStreams.Clear();
        }

        UpdateLoadingState(false);
    }

    public async Task AddUserMessageAndAnswerAsync(string text, ChatConfiguration chatConfiguration, IReadOnlyList<ChatMessageFile>? files = null)
    {
        if (string.IsNullOrWhiteSpace(text) || IsLoading)
        {
            return;
        }

        var trimmedText = text.Trim();
        await AddMessageAsync(new AppChatMessage(trimmedText, DateTime.Now, ChatRole.User, string.Empty, files));
        UpdateLoadingState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            if (_runtime is null)
            {
                _runtime = new InProcessRuntime();
                await _runtime.StartAsync();
            }
            await ProcessAIResponseAsync(chatConfiguration, trimmedText, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleError($"An error occurred while getting the response: {ex.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

  private async Task AddMessageAsync(IAppChatMessage message)
  {
      Messages.Add(message);
      await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
  }


    private async Task ProcessAIResponseAsync(ChatConfiguration chatConfiguration, string userMessage, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing response with model: {ModelName}", chatConfiguration.ModelName);

        List<FunctionCallRecord> functionCalls = [];
        const int updateIntervalMs = 500;
        Dictionary<string, DateTime> lastUpdateTimes = new();

        try
        {
            var trackingFilter = new FunctionCallRecordingFilter(functionCalls);
            List<Kernel> kernels = new();

            try
            {
                if (_runtime != null)
                {
                    _groupChatManager = new ReasonableRoundRobinGroupChatManager(chatConfiguration.StopAgentName, chatConfiguration.StopPhrase)
                    {
                        MaximumInvocationCount = chatConfiguration.MaximumInvocationCount
                    };

                    _agents.Clear();
                    foreach (var desc in _agentDescriptions)
                    {
                        var agentKernel = await kernelService.CreateKernelAsync(
                            chatConfiguration with
                            {
                                Functions = desc.Functions,
                                ModelName = string.IsNullOrWhiteSpace(desc.ModelName)
                                    ? chatConfiguration.ModelName
                                    : desc.ModelName
                            },
                            desc.AutoSelectCount > 0 ? userMessage : null,
                            desc.AutoSelectCount > 0 ? desc.AutoSelectCount : null);

                        agentKernel.FunctionInvocationFilters.Add(trackingFilter);
                        kernels.Add(agentKernel);

                        _agents.Add(new ChatCompletionAgent
                        {
                            Name = !string.IsNullOrWhiteSpace(desc.AgentName) ? desc.AgentName : desc.Name,
                            Description = desc.Name,
                            Instructions = desc.Content,
                            Kernel = agentKernel
                        });
                    }

                    _chatOrchestration = new GroupChatOrchestration(_groupChatManager, _agents.ToArray())
                    {
                        ResponseCallback = async message =>
                        {
                            if (message.Role != AuthorRole.Assistant)
                            {
                                ChatRole role = ChatRole.Assistant;
                                if (message.Role == AuthorRole.System)
                                {
                                    role = ChatRole.System;
                                }
                                else if (message.Role == AuthorRole.User)
                                {
                                    role = ChatRole.User;
                                }

                                var appMessage = new AppChatMessage(message.Content ?? string.Empty, DateTime.Now, role, message.AuthorName ?? string.Empty);
                                await AddMessageAsync(appMessage);
                                return;
                            }

                            var agentName = message.AuthorName ?? string.Empty;
                            if (!_activeStreams.TryGetValue(agentName, out var state))
                            {
                                var stream = _streamingManager.CreateStreamingMessage(null, agentName);
                                state = new StreamState(stream, functionCalls.Count);
                                _activeStreams[agentName] = state;
                                lastUpdateTimes[agentName] = DateTime.MinValue;
                                await AddMessageAsync(stream);
                            }

                            if (!string.IsNullOrEmpty(message.Content))
                            {
                                state.Message.Append(message.Content);
                                state.ApproximateTokenCount++;
                                DateTime now = DateTime.Now;
                                if ((now - lastUpdateTimes[agentName]).TotalMilliseconds >= updateIntervalMs)
                                {
                                    lastUpdateTimes[agentName] = now;
                                    if (MessageUpdated != null)
                                    {
                                        await MessageUpdated(state.Message);
                                    }
                                }

                                logger.LogTrace("Agent {Agent}: {Token}", agentName, message.Content);
                            }
                        }
                    };

                    var invokeResult = await _chatOrchestration.InvokeAsync(userMessage, _runtime, cancellationToken);
                    await invokeResult.GetValueAsync(TimeSpan.FromSeconds(30));
                }
            }
            finally
            {
                foreach (var kernel in kernels)
                {
                    kernel.FunctionInvocationFilters.Remove(trackingFilter);
                }
            }
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            logger.LogWarning(ex, "Model {ModelName} does not support function calling", chatConfiguration.ModelName);
            foreach (var state in _activeStreams.Values.ToList())
            {
                RemoveStreamingMessage(state.Message);
            }
            _activeStreams.Clear();

            string errorMessage = chatConfiguration.Functions.Any()
                ? $"⚠️ The model **{chatConfiguration.ModelName}** does not support function calling. Please either:\n\n" +
                  "• Switch to a model that supports function calling\n" +
                  "• Disable all functions for this conversation\n\n" +
                  "You can see which models support function calling on the Models page."
                : $"⚠️ The model **{chatConfiguration.ModelName}** does not support the requested functionality.";

            await HandleError(errorMessage);
        }
        catch (Exception ex)
        {
            const string errorPrefix = "Agent Error";
            logger.LogError(ex, "Error processing response");
            foreach (var state in _activeStreams.Values.ToList())
            {
                RemoveStreamingMessage(state.Message);
            }
            _activeStreams.Clear();
            await HandleError($"{errorPrefix}: {ex.Message}");
        }
        finally
        {
            foreach (var kvp in _activeStreams.ToList())
            {
                var state = kvp.Value;
                await (MessageUpdated?.Invoke(state.Message) ?? Task.CompletedTask);

                var messageFunctionCalls = functionCalls.Skip(state.FunctionCallStartIndex).ToList();
                foreach (var fc in messageFunctionCalls)
                {
                    state.Message.AddFunctionCall(fc);
                }

                TimeSpan processingTime = DateTime.Now - state.StartTime;
                var statistics = _streamingManager.BuildStatistics(
                    processingTime,
                    chatConfiguration,
                    state.ApproximateTokenCount,
                    messageFunctionCalls.Select(fc => fc.Server).Distinct());

                var finalMessage = _streamingManager.CompleteStreaming(state.Message, statistics);
                await ReplaceStreamingMessageWithFinal(state.Message, finalMessage);
                _activeStreams.Remove(kvp.Key);
            }
        }
    }



    private async Task ReplaceStreamingMessageWithFinal(StreamingAppChatMessage streamingMessage, AppChatMessage finalMessage)
    {
        int index = Messages.IndexOf(streamingMessage);

        if (index >= 0)
        {
            Messages[index] = finalMessage;
            await (MessageUpdated?.Invoke(finalMessage) ?? Task.CompletedTask);
        }
    }

    private void RemoveStreamingMessage(StreamingAppChatMessage streamingMessage)
    {
        Messages.Remove(streamingMessage);
    }

    private async Task HandleError(string text)
    {
        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.Assistant, string.Empty));
    }
    private void Cleanup()
    {
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        UpdateLoadingState(false);
    }

    private void UpdateLoadingState(bool isLoading)
    {
        IsLoading = isLoading;
        LoadingStateChanged?.Invoke(isLoading);
    }

    public async Task DeleteMessageAsync(Guid id)
    {
        if (IsLoading)
            return;

        var message = Messages.FirstOrDefault(m => m.Id == id);
        if (message == null)
            return;

        Messages.Remove(message);
        await (MessageDeleted?.Invoke(id) ?? Task.CompletedTask);
    }
}
#pragma warning restore SKEXP0110

/// <summary>
/// Filter for tracking invoked MCP servers during a chat session.
/// </summary>
file sealed class FunctionCallRecordingFilter(List<FunctionCallRecord> records) : IFunctionInvocationFilter
{
    private readonly List<FunctionCallRecord> _records = records;

    public async Task OnFunctionInvocationAsync(Microsoft.SemanticKernel.FunctionInvocationContext context, Func<Microsoft.SemanticKernel.FunctionInvocationContext, Task> next)
    {
        string request = string.Join(", ", context.Arguments.Select(a => $"{a.Key}: {a.Value}"));
        await next(context);

        string response = context.Result?.GetValue<object>()?.ToString() ?? context.Result?.ToString() ?? string.Empty;
        string server = context.Function.PluginName ?? "McpServer";
        string function = context.Function.Name;
        _records.Add(new FunctionCallRecord(server, function, request, response));
    }
}
