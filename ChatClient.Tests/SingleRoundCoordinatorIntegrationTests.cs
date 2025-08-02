using System.Collections.Generic;
using System;
using System.Linq;
using System.Text;
using ChatClient.Api.Services;
using ChatClient.Shared.LlmAgents;
using ChatClient.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Tests;

public class SingleRoundCoordinatorIntegrationTests
{
    private class SystemPromptKernelLlmAgent : LlmAgentBase
    {
        public SystemPromptKernelLlmAgent(string name, SystemPrompt prompt)
            : base(name, prompt)
        {
        }

        public override async IAsyncEnumerable<StreamingChatMessageContent> GetResponseAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings promptExecutionSettings,
            Kernel kernel,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var fullHistory = new ChatHistory();
            if (AgentDescription is not null)
            {
                var items = new ChatMessageContentItemCollection();
                items.Add(new Microsoft.SemanticKernel.TextContent(AgentDescription.Content));
                fullHistory.Add(new ChatMessageContent(AuthorRole.System, items));
            }
            foreach (var message in chatHistory)
            {
                fullHistory.Add(message);
            }

            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            await foreach (var content in chatService.GetStreamingChatMessageContentsAsync(fullHistory, promptExecutionSettings, kernel, cancellationToken))
            {
                if (content.Content is not null)
                {
                    yield return content;
                }
            }
        }
    }

    private class TestTranslationChatService : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object> Attributes { get; } = new Dictionary<string, object>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? promptExecutionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var text = Translate(chatHistory);
            var items = new ChatMessageContentItemCollection();
            items.Add(new Microsoft.SemanticKernel.TextContent(text));
            ChatMessageContent message = new ChatMessageContent(AuthorRole.Assistant, items);
            IReadOnlyList<ChatMessageContent> list = new[] { message };
            return Task.FromResult(list);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? promptExecutionSettings = null,
            Kernel? kernel = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = Translate(chatHistory);
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, text, null, 0, null, Encoding.UTF8, null);
            await Task.CompletedTask;
        }

        private static string GetText(ChatMessageContent message) =>
            string.Concat(message.Items.OfType<Microsoft.SemanticKernel.TextContent>().Select(t => t.Text));

        private static bool IsRussian(string text) =>
            text.Any(c => c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я');

        private static string Translate(ChatHistory chatHistory)
        {
            var system = GetText(chatHistory[0]);
            var last = GetText(chatHistory[^1]);

            if (system.Contains("English to Russian", StringComparison.OrdinalIgnoreCase))
            {
                return last.Equals("hello", StringComparison.OrdinalIgnoreCase) ? "привет" : last;
            }
            if (system.Contains("Russian to French", StringComparison.OrdinalIgnoreCase))
            {
                return last == "привет" ? "bonjour" : last;
            }
            if (system.Contains("Russian to English", StringComparison.OrdinalIgnoreCase))
            {
                return IsRussian(last) ? (last == "привет" ? "hello" : last) : string.Empty;
            }
            return string.Empty;
        }
    }

    [Fact]
    public async Task Agents_translate_through_single_round()
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService, TestTranslationChatService>();
        var kernel = builder.Build();

        var prompt1 = new SystemPrompt { Name = "e2r", Content = "Translate the last message from English to Russian", AgentName = "A1" };
        var prompt2 = new SystemPrompt { Name = "r2f", Content = "Translate the last message from Russian to French", AgentName = "A2" };

        var agent1 = new SystemPromptKernelLlmAgent("eng-rus", prompt1);
        var agent2 = new SystemPromptKernelLlmAgent("rus-fr", prompt2);

        var coordinator = new SingleRoundLlmAgentCoordinator(new[] { agent1, agent2 });

        var history = new ChatHistory();
        var userItems = new ChatMessageContentItemCollection();
        userItems.Add(new Microsoft.SemanticKernel.TextContent("hello"));
        history.Add(new ChatMessageContent(AuthorRole.User, userItems));

        var settings = new PromptExecutionSettings();
        int cycleCount = 0;
        while (coordinator.ShouldContinueConversation(cycleCount))
        {
            var agent = coordinator.GetNextAgent();
            await foreach (var content in agent.GetResponseAsync(history, settings, kernel))
            {
                if (content.Content is not null)
                {
                    var items = new ChatMessageContentItemCollection();
                    items.Add(new Microsoft.SemanticKernel.TextContent(content.Content));
                    history.Add(new ChatMessageContent(AuthorRole.Assistant, items, agent.Name));
                }
            }
            cycleCount++;
        }

        var last = history[^1];
        var lastText = string.Concat(last.Items.OfType<Microsoft.SemanticKernel.TextContent>().Select(t => t.Text));
        Assert.Equal("bonjour", lastText);
    }
}
