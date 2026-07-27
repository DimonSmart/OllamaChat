namespace ChatClient.Api.Services.Rag;

public interface IDocumentMarkdownConverter
{
    Task<string> ConvertAsync(string fileName, Stream content, string? contentType, CancellationToken cancellationToken);
}
