using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.AspNetCore.Http;

namespace ChatClient.Api.Services;

public class NoOpFileConverter : IFileConverter
{
    private static readonly string[] _extensions = [".txt", ".md"];

    public IEnumerable<string> GetSupportedExtensions() => _extensions;

    public async Task<string> ConvertToTextAsync(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName);
        if (!_extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported file type {ext}");

        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync();
    }
}

