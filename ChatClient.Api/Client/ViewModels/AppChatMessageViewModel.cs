using ChatClient.Api.Client.Markdown;
using ChatClient.Domain.Models;
using DimonSmart.AiUtils;
using Markdig;
using MarkdigMarkdown = Markdig.Markdown;
using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
    public IReadOnlyCollection<string> HtmlFunctionCalls { get; set; } = [];
    public DateTime MsgDateTime { get; set; }
    public ChatRole Role { get; set; }
    public string? Statistics { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsCanceled { get; set; }
    public IReadOnlyList<AppChatMessageFile> Files { get; set; } = [];
    public string? AgentName { get; set; }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .UseSlashParensMath()
        .Build();
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
            .Select(segment => MarkdigMarkdown.ToHtml(segment, Pipeline))
            .ToList()
            .AsReadOnly();
        Content = result.Answer;
        HtmlContent = MarkdigMarkdown.ToHtml(result.Answer, Pipeline);

        HtmlFunctionCalls = FunctionCalls
            .Select(call =>
            {
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(call.Server) || !string.IsNullOrEmpty(call.Function))
                {
                    sb.Append("**");
                    if (!string.IsNullOrEmpty(call.Server))
                    {
                        sb.Append(call.Server);
                    }
                    if (!string.IsNullOrEmpty(call.Function))
                    {
                        if (!string.IsNullOrEmpty(call.Server))
                            sb.Append('.');
                        sb.Append(call.Function);
                    }
                    sb.AppendLine("**  ");
                }
                sb.AppendLine("**Request:**");
                sb.AppendLine("```");
                sb.AppendLine(call.Request);
                sb.AppendLine("```");
                sb.AppendLine("**Response:**");
                sb.AppendLine("```");
                sb.AppendLine(call.Response);
                sb.AppendLine("```");
                return MarkdigMarkdown.ToHtml(sb.ToString(), Pipeline);
            })
            .ToList()
            .AsReadOnly();

        return this;
    }

    public static AppChatMessageViewModel CreateFromDomainModel(IAppChatMessage message) => new AppChatMessageViewModel().UpdateFromDomainModel(message);
}
