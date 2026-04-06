namespace ChatClient.Api.Services.BuiltIn;

public sealed record MarkdownDocumentIntakeResult(
    string Format,
    string? SourceFile,
    string Title,
    string Markdown,
    int LineCount,
    int WordCount,
    int CharacterCount);
