using ChatClient.Api.Search;
using HtmlAgilityPack;
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
        RenderHtml(markdown ?? string.Empty);

    public static string ToHtmlModelOutput(string? markdown) =>
        RenderHtml(UnwrapOuterMarkdownFence(markdown));

    private static string RenderHtml(string markdown) =>
        NormalizeImageSources(Markdig.Markdown.ToHtml(markdown, Pipeline));

    private static string NormalizeImageSources(string html)
    {
        if (string.IsNullOrWhiteSpace(html)
            || !html.Contains("imgs.search.brave.com", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);

        var imageNodes = document.DocumentNode.SelectNodes("//img[@src]");
        if (imageNodes is null)
            return html;

        var changed = false;
        foreach (var imageNode in imageNodes)
        {
            var source = imageNode.GetAttributeValue("src", string.Empty);
            var normalized = SearchResultUrlNormalizer.NormalizeImageUrl(source);
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(source, normalized, StringComparison.Ordinal))
            {
                continue;
            }

            imageNode.SetAttributeValue("src", normalized);
            changed = true;
        }

        return changed
            ? document.DocumentNode.SelectSingleNode("//body")?.InnerHtml ?? document.DocumentNode.InnerHtml
            : html;
    }

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
