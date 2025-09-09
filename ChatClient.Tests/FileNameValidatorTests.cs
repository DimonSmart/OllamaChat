using ChatClient.Api.Services;
using System;

public class FileNameValidatorTests
{
    [Theory]
    [InlineData("valid.txt")]
    [InlineData("another_file.md")]
    public void Validate_AllowsValidNames(string fileName)
        => FileNameValidator.Validate(fileName);

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..")]
    [InlineData("na/me.txt")]
    public void Validate_RejectsInvalidNames(string fileName)
        => Assert.Throws<ArgumentException>(() => FileNameValidator.Validate(fileName));
}
