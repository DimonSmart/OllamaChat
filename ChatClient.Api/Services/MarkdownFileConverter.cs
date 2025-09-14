using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.IO;

namespace ChatClient.Api.Services;

public class MarkdownFileConverter : IFileConverter
{
    private static readonly string[] Extensions = [".md"];

    public IEnumerable<string> GetSupportedExtensions() => Extensions;

    public async Task<string> ConvertToTextAsync(IFormFile file)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync();
    }
}
