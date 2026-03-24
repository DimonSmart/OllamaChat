using ChatClient.Api.Client.Markdown;
using ChatClient.Domain.Models;
using DimonSmart.AiUtils;
using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Linq;

namespace ChatClient.Api.Client.ViewModels;

public class AppChatMessageViewModel
{
    public Guid Id { get; set; }
    public string RawContent { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
    public IReadOnlyCollection<string> ThinkSegments { get; set; } = [];
    public IReadOnlyCollection<string> HtmlThinkSegments { get; set; } = [];
    public IReadOnlyCollection<FunctionCallRecord> FunctionCalls { get; set; } = [];
    public DateTime MsgDateTime { get; set; }
    public ChatRole Role { get; set; }
    public string? Statistics { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsCanceled { get; set; }
    public IReadOnlyList<AppChatMessageFile> Files { get; set; } = [];
    public string? AgentName { get; set; }

    public AppChatMessageViewModel UpdateFromDomainModel(IAppChatMessage message)
    {
        Id = message.Id;
        RawContent = message.Content;
        MsgDateTime = message.MsgDateTime;
        Role = message.Role;
        AgentName = message.AgentName;
        Statistics = message.Statistics;
        IsStreaming = message.IsStreaming;
        IsCanceled = message.IsCanceled;
        Files = message.Files;
        FunctionCalls = message.FunctionCalls;
        var result = ThinkTagParser.ExtractThinkAnswer(message.Content);

        ThinkSegments = result.ThoughtSegments;
        HtmlThinkSegments = result.ThoughtSegments
            .Select(AppMarkdown.ToHtmlModelOutput)
            .ToList()
            .AsReadOnly();
        Content = result.Answer;
        HtmlContent = AppMarkdown.ToHtmlModelOutput(result.Answer);

        return this;
    }

    public static AppChatMessageViewModel CreateFromDomainModel(IAppChatMessage message) => new AppChatMessageViewModel().UpdateFromDomainModel(message);
}
