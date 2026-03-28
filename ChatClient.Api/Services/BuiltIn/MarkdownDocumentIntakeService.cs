using System.Text.RegularExpressions;

namespace ChatClient.Api.Services.BuiltIn;

public sealed class MarkdownDocumentIntakeService
{
    private static readonly Regex HeadingRegex = new(
        @"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public async Task<MarkdownDocumentIntakeResult> ReadDocumentAsync(
        string sourceFile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            throw new InvalidOperationException("source_file_required");
        }

        var absolutePath = Path.GetFullPath(sourceFile.Trim());
        if (!File.Exists(absolutePath))
        {
            throw new InvalidOperationException("source_file_not_found");
        }

        var extension = Path.GetExtension(absolutePath);
        if (!IsMarkdownLikeExtension(extension))
        {
            throw new InvalidOperationException("unsupported_source_format");
        }

        var markdown = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        return PrepareMarkdown(markdown, Path.GetFileNameWithoutExtension(absolutePath), absolutePath);
    }

    public MarkdownDocumentIntakeResult PrepareMarkdown(
        string markdown,
        string? fallbackTitle = null,
        string? sourceFile = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new InvalidOperationException("markdown_required");
        }

        var normalizedMarkdown = NormalizeMarkdown(markdown);
        var title = ResolveTitle(normalizedMarkdown, fallbackTitle);
        var lineCount = CountLines(normalizedMarkdown);
        var wordCount = CountWords(normalizedMarkdown);

        return new MarkdownDocumentIntakeResult(
            Format: "markdown",
            SourceFile: string.IsNullOrWhiteSpace(sourceFile) ? null : Path.GetFullPath(sourceFile),
            Title: title,
            Markdown: normalizedMarkdown,
            LineCount: lineCount,
            WordCount: wordCount,
            CharacterCount: normalizedMarkdown.Length);
    }

    private static bool IsMarkdownLikeExtension(string? extension) =>
        extension switch
        {
            ".md" => true,
            ".markdown" => true,
            ".mdown" => true,
            ".mkd" => true,
            ".txt" => true,
            _ => false
        };

    private static string NormalizeMarkdown(string markdown)
    {
        var normalized = markdown.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("markdown_required");
        }

        return normalized;
    }

    private static string ResolveTitle(string markdown, string? fallbackTitle)
    {
        var match = HeadingRegex.Match(markdown);
        if (match.Success)
        {
            var heading = match.Groups["title"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(heading))
            {
                return heading;
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackTitle))
        {
            return fallbackTitle.Trim();
        }

        return "Markdown Document";
    }

    private static int CountLines(string markdown) =>
        markdown.Split('\n').Length;

    private static int CountWords(string markdown) =>
        markdown
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
}
