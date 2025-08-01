using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ChatClient.Api.Client.MarkdownExtensions;

/// <summary>
/// Inline parser supporting LaTeX math delimited by \( and \).
/// Produces <see cref="MathInline"/> elements for Markdig.
/// </summary>
public class SlashParensMathInlineParser : InlineParser
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SlashParensMathInlineParser"/> class.
    /// </summary>
    public SlashParensMathInlineParser()
    {
        OpeningCharacters = ['\\'];
        DefaultClass = "math";
    }

    /// <summary>
    /// Gets or sets the default CSS class for created math inlines.
    /// </summary>
    public string? DefaultClass { get; set; }

    /// <inheritdoc />
    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var startPosition = slice.Start;
        if (slice.CurrentChar != '\\' || slice.PeekChar() != '(')
        {
            return false;
        }

        // Skip the opening '\(' sequence
        slice.NextChar(); // skip '\'
        slice.NextChar(); // skip '('

        var contentStart = slice.Start;
        char previous = '\0';
        char c = slice.CurrentChar;
        while (c != '\0')
        {
            if (c == '\\' && slice.PeekChar() == ')' && previous != '\\')
            {
                // Found closing '\)'
                var inline = new MathInline
                {
                    Span = new SourceSpan(
                        processor.GetSourcePosition(startPosition, out int line, out int column),
                        processor.GetSourcePosition(slice.Start + 1)),
                    Line = line,
                    Column = column,
                    Delimiter = '\\',
                    DelimiterCount = 1,
                    Content = new StringSlice(slice.Text, contentStart, slice.Start - 1)
                };
                processor.Inline = inline;

                // Skip the closing '\)' sequence
                slice.NextChar();
                slice.NextChar();
                return true;
            }

            if (c == '\n' || c == '\r')
            {
                // New lines are not allowed inside inline math
                slice.Start = startPosition;
                return false;
            }

            previous = c;
            c = slice.NextChar();
        }

        // No closing delimiter found, rewind
        slice.Start = startPosition;
        return false;
    }
}
