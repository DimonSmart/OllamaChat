using System.Net;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ChatClient.Api.Client.Components;

internal static class WorkflowSourceCodeSyntaxHighlighter
{
    private const string KeywordClass = "tok-keyword";
    private const string StringClass = "tok-string";
    private const string NumberClass = "tok-number";
    private const string CommentClass = "tok-comment";
    private const string IdentifierClass = "tok-identifier";
    private const string PunctuationClass = "tok-punctuation";
    private const string ErrorClass = "tok-error";
    private const string EmptyPlaceholder = "&nbsp;";

    public static string ToHighlightedHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EmptyPlaceholder;
        }

        var builder = new StringBuilder(text.Length * 2);

        foreach (var token in SyntaxFactory.ParseTokens(text))
        {
            AppendTrivia(builder, token.LeadingTrivia);
            AppendToken(builder, token);
            AppendTrivia(builder, token.TrailingTrivia);
        }

        return builder.Length == 0
            ? EmptyPlaceholder
            : builder.ToString();
    }

    private static void AppendTrivia(StringBuilder builder, SyntaxTriviaList triviaList)
    {
        foreach (var trivia in triviaList)
        {
            var cssClass = GetTriviaClass(trivia);
            if (cssClass is null)
            {
                builder.Append(WebUtility.HtmlEncode(trivia.ToFullString()));
                continue;
            }

            AppendSpan(builder, cssClass, trivia.ToFullString());
        }
    }

    private static void AppendToken(StringBuilder builder, SyntaxToken token)
    {
        if (string.IsNullOrEmpty(token.Text))
        {
            return;
        }

        AppendSpan(builder, GetTokenClass(token), token.Text);
    }

    private static string? GetTriviaClass(SyntaxTrivia trivia)
    {
        return trivia.Kind() switch
        {
            SyntaxKind.SingleLineCommentTrivia => CommentClass,
            SyntaxKind.MultiLineCommentTrivia => CommentClass,
            SyntaxKind.SingleLineDocumentationCommentTrivia => CommentClass,
            SyntaxKind.MultiLineDocumentationCommentTrivia => CommentClass,
            SyntaxKind.DocumentationCommentExteriorTrivia => CommentClass,
            _ => null
        };
    }

    private static string GetTokenClass(SyntaxToken token)
    {
        if (SyntaxFacts.IsKeywordKind(token.Kind()))
        {
            return KeywordClass;
        }

        if (IsStringToken(token.Kind()))
        {
            return StringClass;
        }

        return token.Kind() switch
        {
            SyntaxKind.NumericLiteralToken => NumberClass,
            SyntaxKind.IdentifierToken => IdentifierClass,
            SyntaxKind.BadToken => ErrorClass,
            _ => PunctuationClass
        };
    }

    private static bool IsStringToken(SyntaxKind kind)
    {
        var kindName = kind.ToString();
        return kindName.Contains("StringLiteralToken", StringComparison.Ordinal)
            || kindName.Contains("CharacterLiteralToken", StringComparison.Ordinal)
            || kindName.Contains("Interpolated", StringComparison.Ordinal);
    }

    private static void AppendSpan(StringBuilder builder, string cssClass, string text)
    {
        builder
            .Append("<span class=\"")
            .Append(cssClass)
            .Append("\">")
            .Append(WebUtility.HtmlEncode(text))
            .Append("</span>");
    }
}
