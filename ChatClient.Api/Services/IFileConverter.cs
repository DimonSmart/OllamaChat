using Microsoft.AspNetCore.Http;
using System.Collections.Generic;

namespace ChatClient.Api.Services;

public interface IFileConverter
{
    Task<string> ConvertToTextAsync(IFormFile file);
    IEnumerable<string> GetSupportedExtensions();
}

