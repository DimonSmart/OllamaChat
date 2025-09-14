using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UglyToad.PdfPig;

namespace ChatClient.Api.Services;

public class FileConverter : IFileConverter
{
    private static readonly string[] Extensions = [".txt", ".docx", ".pdf"];

    public IEnumerable<string> GetSupportedExtensions() => Extensions;

    public async Task<string> ConvertToTextAsync(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName);
        using var stream = file.OpenReadStream();
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return await new StreamReader(stream).ReadToEndAsync();
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return ExtractDocx(stream);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return ExtractPdf(stream);
        throw new NotSupportedException($"Unsupported file type {extension}");
    }

    private static string ExtractDocx(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var builder = new StringBuilder();
        var body = doc.MainDocumentPart?.Document;
        if (body is null)
            return string.Empty;
        foreach (var text in body.Descendants<Text>())
            builder.AppendLine(text.Text);
        return builder.ToString();
    }

    private static string ExtractPdf(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        var builder = new StringBuilder();
        foreach (var page in pdf.GetPages())
            builder.AppendLine(page.Text);
        return builder.ToString();
    }
}

