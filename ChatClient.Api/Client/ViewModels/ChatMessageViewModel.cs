using System.Collections.Generic;
using System.Linq;
using System.Text;

using ChatClient.Shared.Models;

using DimonSmart.AiUtils;

using Markdig;

using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.ViewModels;

public class ChatMessageViewModel
{
    public Guid Id { get; set; }
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
    public IReadOnlyList<ChatMessageFile> Files { get; set; } = [];

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();
    private ChatMessageViewModel Populate(IAppChatMessage message)
    {
        Id = message.Id;
        Content = message.Content;
        MsgDateTime = message.MsgDateTime;
        Role = message.Role;
        Statistics = message.Statistics;
        IsStreaming = message.IsStreaming;
        IsCanceled = message.IsCanceled;
        Files = message.Files;
        FunctionCalls = message.FunctionCalls;
        var result = ThinkTagParser.ExtractThinkAnswer(message.Content);

        ThinkSegments = result.ThoughtSegments;
        HtmlThinkSegments = result.ThoughtSegments
            .Select(segment => Markdown.ToHtml(segment, Pipeline))
            .ToList()
            .AsReadOnly();
        Content = result.Answer;
        HtmlContent = Markdown.ToHtml(result.Answer, Pipeline);

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
                return Markdown.ToHtml(sb.ToString(), Pipeline);
            })
            .ToList()
            .AsReadOnly();

        return this;
    }

    public static ChatMessageViewModel CreateFromDomainModel(IAppChatMessage message) => new ChatMessageViewModel().Populate(message);

    public void UpdateFromDomainModel(IAppChatMessage message) => Populate(message);
}
