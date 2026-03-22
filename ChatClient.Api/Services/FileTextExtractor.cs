using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using System.Text;

namespace ChatClient.Api.Services;

public static class FileTextExtractor
{
    public static bool Supports(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> ExtractTextAsync(
        string fileName,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return ExtractDocx(stream);

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return ExtractPdf(stream);

        throw new NotSupportedException($"Unsupported file type {extension}");
    }

    private static string ExtractDocx(Stream stream)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var builder = new StringBuilder();
        var body = document.MainDocumentPart?.Document;
        if (body is null)
            return string.Empty;

        foreach (var text in body.Descendants<Text>())
        {
            builder.AppendLine(text.Text);
        }

        return builder.ToString();
    }

    private static string ExtractPdf(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        var builder = new StringBuilder();

        foreach (var page in pdf.GetPages())
        {
            builder.AppendLine(page.Text);
        }

        return builder.ToString();
    }
}
