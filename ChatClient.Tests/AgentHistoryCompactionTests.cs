#pragma warning disable MAAI001
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public class AgentHistoryCompactionTests
{
    [Fact]
    public async Task NamedToolWindowCompactionStrategy_RemovesOlderMatchingToolGroups()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "Start reading the cursor."),
            CreateToolCall("call-1", "cursor_next"),
            new(ChatRole.Tool, "paragraph-1"),
            CreateToolCall("call-2", "save_registry"),
            new(ChatRole.Tool, "saved"),
            CreateToolCall("call-3", "cursor_next"),
            new(ChatRole.Tool, "paragraph-2"),
            CreateToolCall("call-4", "cursor_next"),
            new(ChatRole.Tool, "paragraph-3")
        ];

        var strategy = new NamedToolWindowCompactionStrategy(["cursor_next"], keepLastToolPairs: 2);
        var included = (await CompactionProvider.CompactAsync(strategy, messages, NullLogger.Instance))
            .ToList();

        Assert.DoesNotContain(included, message => message.Role == ChatRole.Tool && message.Text == "paragraph-1");
        Assert.Contains(included, message => message.Role == ChatRole.Tool && message.Text == "saved");
        Assert.Contains(included, message => message.Role == ChatRole.Tool && message.Text == "paragraph-2");
        Assert.Contains(included, message => message.Role == ChatRole.Tool && message.Text == "paragraph-3");
    }

    [Fact]
    public void AgentHistoryCompactionFactory_ResolvesOriginalToolNames_ToRegisteredNames()
    {
        var toolSet = CreateToolSet(
            new AgenticRegisteredTool(
                RegisteredName: "book_cursor__cursor_next",
                ServerName: "markdown-document",
                ToolName: "cursor_next",
                MayRequireUserInput: false,
                Tool: Mock.Of<AITool>()),
            new AgenticRegisteredTool(
                RegisteredName: "registry__save_registry",
                ServerName: "character-registry",
                ToolName: "save_registry",
                MayRequireUserInput: false,
                Tool: Mock.Of<AITool>()));

        var agent = new AgentDescription
        {
            AgentName = "Reader",
            ExecutionSettings = new AgentExecutionSettings
            {
                HistoryCompaction = new AgentHistoryCompactionSettings
                {
                    Enabled = true,
                    Mode = AgentHistoryCompactionModes.ToolWindow,
                    KeepLastToolPairs = 5,
                    ToolNames = ["cursor_next"]
                }
            }
        };

        var attachment = AgentHistoryCompactionFactory.Create(
            agent,
            toolSet,
            NullLoggerFactory.Instance,
            NullLogger.Instance);

        Assert.NotNull(attachment);
        Assert.Contains("book_cursor__cursor_next", attachment.RegisteredToolNames);
        Assert.DoesNotContain("registry__save_registry", attachment.RegisteredToolNames);
        Assert.Contains("Only the most recent 5 matching tool result pair(s) remain visible.", attachment.InstructionNote);
    }

    [Fact]
    public void AgentHistoryCompactionFactory_ReturnsNull_WhenConfiguredToolsDoNotMatchRuntimeTools()
    {
        var toolSet = CreateToolSet(
            new AgenticRegisteredTool(
                RegisteredName: "registry__save_registry",
                ServerName: "character-registry",
                ToolName: "save_registry",
                MayRequireUserInput: false,
                Tool: Mock.Of<AITool>()));

        var agent = new AgentDescription
        {
            AgentName = "Reader",
            ExecutionSettings = new AgentExecutionSettings
            {
                HistoryCompaction = new AgentHistoryCompactionSettings
                {
                    Enabled = true,
                    Mode = AgentHistoryCompactionModes.ToolWindow,
                    KeepLastToolPairs = 5,
                    ToolNames = ["cursor_next"]
                }
            }
        };

        var attachment = AgentHistoryCompactionFactory.Create(
            agent,
            toolSet,
            NullLoggerFactory.Instance,
            NullLogger.Instance);

        Assert.Null(attachment);
    }

    private static AgenticToolSet CreateToolSet(params AgenticRegisteredTool[] tools)
    {
        var byName = tools.ToDictionary(
            static tool => tool.RegisteredName,
            static tool => tool,
            StringComparer.OrdinalIgnoreCase);

        return new AgenticToolSet(
            tools.Select(static tool => tool.Tool).ToList(),
            byName);
    }

    private static ChatMessage CreateToolCall(string callId, string toolName) =>
        new(
            ChatRole.Assistant,
            new List<AIContent>
            {
                new FunctionCallContent(callId, toolName, new Dictionary<string, object?>())
            });
}
#pragma warning restore MAAI001
