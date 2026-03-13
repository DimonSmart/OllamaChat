using Markdig;

namespace ChatClient.Api.Client.Markdown;

public static class AppMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .UseSlashParensMath()
        .Build();

    public static string ToHtml(string? markdown) =>
        Markdig.Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
}
