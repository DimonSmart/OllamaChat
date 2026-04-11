namespace ChatClient.Domain.Models;

public enum VoiceInputInitializationStatus
{
    NotInitialized = 0,
    Initializing = 1,
    Ready = 2,
    Error = 3
}
