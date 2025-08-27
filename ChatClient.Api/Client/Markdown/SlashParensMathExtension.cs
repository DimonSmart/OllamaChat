using Markdig;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;

namespace ChatClient.Api.Client.MarkdownExtensions;

/// <summary>
/// Markdown extension that enables parsing LaTeX inline math using \( and \) delimiters.
/// </summary>
public class SlashParensMathExtension : IMarkdownExtension
{
    /// <inheritdoc />
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<SlashParensMathInlineParser>())
        {
            // Insert before the escape parser so that backslashes are preserved
            pipeline.InlineParsers.InsertBefore<EscapeInlineParser>(new SlashParensMathInlineParser());
        }
    }

    /// <inheritdoc />
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // Rendering handled by default math renderers from UseMathematics
    }
}

/// <summary>
/// Helper methods for enabling <see cref="SlashParensMathExtension"/>.
/// </summary>
public static class SlashParensMathExtensionMethods
{
    public static MarkdownPipelineBuilder UseSlashParensMath(this MarkdownPipelineBuilder pipeline)
    {
        pipeline.Extensions.AddIfNotAlready<SlashParensMathExtension>();
        return pipeline;
    }
}
