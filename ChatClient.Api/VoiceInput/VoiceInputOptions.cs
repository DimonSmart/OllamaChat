namespace ChatClient.Api.VoiceInput;

public sealed class VoiceInputOptions
{
    public const string SectionName = "VoiceInput";

    public string DirectoryPath { get; set; } = string.Empty;

    public string ModelType { get; set; } = "Base";

    public string RecognitionLanguage { get; set; } = "auto";

    public long MaxAudioBytes { get; set; } = 25 * 1024 * 1024;
}
