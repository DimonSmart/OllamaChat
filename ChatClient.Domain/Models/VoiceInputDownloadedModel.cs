namespace ChatClient.Domain.Models;

public sealed record VoiceInputDownloadedModel(
    string ModelType,
    string DisplayName,
    string FileName,
    long SizeBytes);
