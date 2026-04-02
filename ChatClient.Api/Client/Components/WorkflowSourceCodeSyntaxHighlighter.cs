using System.Net;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ChatClient.Api.Client.Components;

internal static class WorkflowSourceCodeSyntaxHighlighter
{
    private const string AgentDisplayNameClass = "tok-agent-display-name";
    private const string AgentFactoryMethodClass = "tok-agent-factory";
    private const string AgentIdClass = "tok-agent-id";
    private const string AgentReferenceMethodClass = "tok-agent-reference";
    private const string AgentRegistrationMethodClass = "tok-agent-registration";
    private const string KeywordClass = "tok-keyword";
    private const string ManagerConfigMethodClass = "tok-manager-config";
    private const string ManagerIdClass = "tok-manager-id";
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

        var dslHighlights = AnalyzeDsl(text);
        var builder = new StringBuilder(text.Length * 2);

        foreach (var token in SyntaxFactory.ParseTokens(text))
        {
            AppendTrivia(builder, token.LeadingTrivia);
            AppendToken(builder, token, dslHighlights);
            AppendTrivia(builder, token.TrailingTrivia);
        }

        return builder.Length == 0
            ? EmptyPlaceholder
            : builder.ToString();
    }

    private static DslHighlights AnalyzeDsl(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetRoot();
        var tokenClasses = new Dictionary<int, string>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodToken = GetInvokedMethodIdentifier(invocation);
            if (methodToken is null || methodToken.Value.RawKind == 0)
            {
                continue;
            }

            var method = methodToken.Value;

            switch (method.ValueText)
            {
                case "Agent":
                    AddTokenClass(tokenClasses, method, AgentRegistrationMethodClass);
                    AddStringLiteralArgument(invocation, 0, AgentIdClass, tokenClasses);
                    break;

                case "AgentFromSaved":
                    AddTokenClass(tokenClasses, method, AgentRegistrationMethodClass);
                    AddStringLiteralArgument(invocation, 0, AgentDisplayNameClass, tokenClasses);
                    break;

                case "Id" when IsNestedInsideAgentRegistration(invocation):
                    AddTokenClass(tokenClasses, method, AgentRegistrationMethodClass);
                    AddStringLiteralArgument(invocation, 0, AgentIdClass, tokenClasses);
                    break;

                case "New" when IsAgentDefinitionBuilderNew(invocation):
                    AddTokenClass(tokenClasses, method, AgentFactoryMethodClass);
                    AddStringLiteralArgument(invocation, 0, AgentDisplayNameClass, tokenClasses);
                    AddStringLiteralArgument(invocation, 1, AgentIdClass, tokenClasses);
                    break;

                case "UseDraft":
                    AddTokenClass(tokenClasses, method, AgentFactoryMethodClass);
                    break;

                case "Participant":
                case "Participants":
                    AddTokenClass(tokenClasses, method, AgentReferenceMethodClass);
                    AddAllStringLiteralArguments(invocation, AgentIdClass, tokenClasses);
                    break;

                case "StartWith":
                    AddTokenClass(tokenClasses, method, AgentReferenceMethodClass);
                    AddStringLiteralArgument(invocation, 0, AgentIdClass, tokenClasses);
                    break;

                case "Handoff":
                case "Fallback":
                    AddTokenClass(tokenClasses, method, AgentReferenceMethodClass);
                    AddStringLiteralArguments(invocation, AgentIdClass, tokenClasses, 0, 1);
                    break;

                case "UseCustomManager":
                    AddTokenClass(tokenClasses, method, ManagerConfigMethodClass);
                    AddStringLiteralArgument(invocation, 0, ManagerIdClass, tokenClasses);
                    break;

                case "UseRoundRobinManager":
                    AddTokenClass(tokenClasses, method, ManagerConfigMethodClass);
                    break;
            }
        }

        return new DslHighlights(tokenClasses);
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

    private static void AppendToken(StringBuilder builder, SyntaxToken token, DslHighlights dslHighlights)
    {
        if (string.IsNullOrEmpty(token.Text))
        {
            return;
        }

        AppendSpan(builder, GetTokenClass(token, dslHighlights), token.Text);
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

    private static string GetTokenClass(SyntaxToken token, DslHighlights dslHighlights)
    {
        if (dslHighlights.TryGetTokenClass(token, out var dslClass))
        {
            return dslClass;
        }

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

    private static SyntaxToken? GetInvokedMethodIdentifier(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier,
            GenericNameSyntax generic => generic.Identifier,
            MemberAccessExpressionSyntax { Name: SimpleNameSyntax name } => name.Identifier,
            MemberBindingExpressionSyntax { Name: SimpleNameSyntax name } => name.Identifier,
            _ => null
        };
    }

    private static bool IsAgentDefinitionBuilderNew(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.ValueText: "AgentDefinitionBuilder" },
            Name: SimpleNameSyntax { Identifier.ValueText: "New" }
        };
    }

    private static bool IsNestedInsideAgentRegistration(InvocationExpressionSyntax invocation)
    {
        return invocation
            .Ancestors()
            .OfType<InvocationExpressionSyntax>()
            .Any(static ancestor =>
            {
                var methodToken = GetInvokedMethodIdentifier(ancestor);
                if (methodToken is null || methodToken.Value.RawKind == 0)
                {
                    return false;
                }

                var method = methodToken.Value;
                return string.Equals(method.ValueText, "Agent", StringComparison.Ordinal)
                    || string.Equals(method.ValueText, "AgentFromSaved", StringComparison.Ordinal);
            });
    }

    private static void AddStringLiteralArguments(
        InvocationExpressionSyntax invocation,
        string cssClass,
        Dictionary<int, string> tokenClasses,
        params int[] indices)
    {
        foreach (var index in indices)
        {
            AddStringLiteralArgument(invocation, index, cssClass, tokenClasses);
        }
    }

    private static void AddAllStringLiteralArguments(
        InvocationExpressionSyntax invocation,
        string cssClass,
        Dictionary<int, string> tokenClasses)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (TryGetStringLiteralToken(argument.Expression, out var token))
            {
                AddTokenClass(tokenClasses, token, cssClass);
            }
        }
    }

    private static void AddStringLiteralArgument(
        InvocationExpressionSyntax invocation,
        int index,
        string cssClass,
        Dictionary<int, string> tokenClasses)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (index < 0 || index >= arguments.Count)
        {
            return;
        }

        if (TryGetStringLiteralToken(arguments[index].Expression, out var token))
        {
            AddTokenClass(tokenClasses, token, cssClass);
        }
    }

    private static bool TryGetStringLiteralToken(ExpressionSyntax expression, out SyntaxToken token)
    {
        if (expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            token = literal.Token;
            return true;
        }

        token = default;
        return false;
    }

    private static void AddTokenClass(
        Dictionary<int, string> tokenClasses,
        SyntaxToken token,
        string cssClass)
    {
        if (string.IsNullOrEmpty(token.Text))
        {
            return;
        }

        tokenClasses[token.SpanStart] = cssClass;
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

    private sealed record DslHighlights(IReadOnlyDictionary<int, string> TokenClasses)
    {
        public bool TryGetTokenClass(SyntaxToken token, out string cssClass)
        {
            return TokenClasses.TryGetValue(token.SpanStart, out cssClass!);
        }
    }
}
