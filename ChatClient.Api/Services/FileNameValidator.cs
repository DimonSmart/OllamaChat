using System;
using System.IO;

namespace ChatClient.Api.Services;

public static class FileNameValidator
{
    public static void Validate(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName != Path.GetFileName(fileName) ||
            fileName is "." or "..")
            throw new ArgumentException("Invalid file name.", nameof(fileName));
    }
}

