using System.Collections.Generic;

using Microsoft.AspNetCore.Http;

namespace ChatClient.Api.Services;

public interface IFileConverter
{
    Task<string> ConvertToTextAsync(IFormFile file);
    IEnumerable<string> GetSupportedExtensions();
}

