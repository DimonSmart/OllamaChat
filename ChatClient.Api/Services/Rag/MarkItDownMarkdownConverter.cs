using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Reflection;

namespace ChatClient.Api.Services.Rag;

public sealed class MarkItDownMarkdownConverter(
    IOptions<KnowledgeIngestionOptions> options,
    ILoggerFactory loggerFactory) : IDocumentMarkdownConverter
{
    public async Task<string> ConvertAsync(string fileName, Stream content, string? contentType, CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.MarkItDownCommand))
            throw new InvalidOperationException("MarkItDown is unavailable because its launch command is not configured.");

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ollamachat-markitdown-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        try
        {
            await using (var target = File.Create(temporaryPath))
                await content.CopyToAsync(target, cancellationToken);

            await using var client = await McpClient.CreateAsync(
                new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "MarkItDown",
                    Command = configuration.MarkItDownCommand,
                    Arguments = configuration.MarkItDownArguments,
                    WorkingDirectory = AppContext.BaseDirectory
                }, loggerFactory),
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "OllamaChat",
                        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
                    }
                },
                loggerFactory,
                cancellationToken);

            var tool = (await client.ListToolsAsync(cancellationToken: cancellationToken))
                .FirstOrDefault(candidate => string.Equals(candidate.Name, "convert_to_markdown", StringComparison.Ordinal));
            if (tool is null)
                throw new InvalidOperationException("MarkItDown is unavailable because it does not expose convert_to_markdown.");

            var result = await tool.CallAsync(
                new Dictionary<string, object?> { ["uri"] = new Uri(temporaryPath).AbsoluteUri },
                null,
                null);
            if (result.IsError == true)
                throw new InvalidOperationException("MarkItDown reported a conversion error.");

            var markdown = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            if (string.IsNullOrWhiteSpace(markdown))
                throw new InvalidOperationException("MarkItDown returned empty Markdown.");
            return markdown;
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("MarkItDown is unavailable", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"MarkItDown conversion failed for '{fileName}'.", exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
