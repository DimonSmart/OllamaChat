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

    public static string ToHtmlModelOutput(string? markdown) =>
        Markdig.Markdown.ToHtml(UnwrapOuterMarkdownFence(markdown), Pipeline);

    private static string UnwrapOuterMarkdownFence(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        string normalized = markdown.ReplaceLineEndings("\n").Trim();
        string[] lines = normalized.Split('\n');
        if (lines.Length < 3)
        {
            return markdown;
        }

        string openingLine = lines[0].Trim();
        if (!IsMarkdownFence(openingLine))
        {
            return markdown;
        }

        int closingLineIndex = lines.Length - 1;
        while (closingLineIndex > 0 && string.IsNullOrWhiteSpace(lines[closingLineIndex]))
        {
            closingLineIndex--;
        }

        if (!string.Equals(lines[closingLineIndex].Trim(), "```", StringComparison.Ordinal))
        {
            return markdown;
        }

        return string.Join('\n', lines[1..closingLineIndex]);
    }

    private static bool IsMarkdownFence(string line)
    {
        if (!line.StartsWith("```", StringComparison.Ordinal))
        {
            return false;
        }

        string info = line[3..].Trim();
        return info.Equals("markdown", StringComparison.OrdinalIgnoreCase)
            || info.Equals("md", StringComparison.OrdinalIgnoreCase);
    }
}
