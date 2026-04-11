using System.Text;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.Ggml;

namespace ChatClient.Api.VoiceInput;

public sealed class VoiceInputService(
    IConfiguration configuration,
    IOptions<VoiceInputOptions> optionsAccessor,
    IUserSettingsService userSettingsService,
    ILogger<VoiceInputService> logger) : IVoiceInputService, IDisposable
{
    private readonly IUserSettingsService _userSettingsService = userSettingsService;
    private readonly ILogger<VoiceInputService> _logger = logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly VoiceInputOptions _options = optionsAccessor.Value;
    private readonly string _voiceInputDirectoryPath = StoragePathResolver.ResolveUserPath(
        configuration,
        optionsAccessor.Value.DirectoryPath,
        FilePathConstants.DefaultVoiceInputDirectory);
    private readonly GgmlType _modelType = ResolveModelType(optionsAccessor.Value.ModelType);

    private WhisperFactory? _factory;

    public async Task<VoiceInputSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var userSettings = await _userSettingsService.GetSettingsAsync(cancellationToken);
        var voiceInput = userSettings.VoiceInput ??= new VoiceInputSettings();
        NormalizeVoiceInputSettings(voiceInput);
        var updated = false;

        if (voiceInput.Status == VoiceInputInitializationStatus.Initializing)
        {
            voiceInput.Status = VoiceInputInitializationStatus.NotInitialized;
            voiceInput.ErrorMessage = string.Empty;
            updated = true;
        }

        if (voiceInput.Status == VoiceInputInitializationStatus.Ready && !File.Exists(GetModelFilePath()))
        {
            voiceInput.Status = VoiceInputInitializationStatus.NotInitialized;
            voiceInput.ErrorMessage = string.Empty;
            DisposeFactory();
            updated = true;
        }

        if (updated)
        {
            await _userSettingsService.SaveSettingsAsync(userSettings, cancellationToken);
        }

        return voiceInput;
    }

    public async Task<VoiceInputSettings> InitializeAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var userSettings = await _userSettingsService.GetSettingsAsync(cancellationToken);
            var voiceInput = userSettings.VoiceInput ??= new VoiceInputSettings();
            NormalizeVoiceInputSettings(voiceInput);

            voiceInput.Status = VoiceInputInitializationStatus.Initializing;
            voiceInput.ErrorMessage = string.Empty;
            await _userSettingsService.SaveSettingsAsync(userSettings, cancellationToken);
            progress?.Report("Checking local voice runtime...");

            try
            {
                progress?.Report("Preparing voice input directory...");
                Directory.CreateDirectory(_voiceInputDirectoryPath);
                await EnsureModelExistsAsync(progress, cancellationToken);
                await EnsureFactoryLoadedAsync(forceReload: true, progress, cancellationToken);

                voiceInput.Status = VoiceInputInitializationStatus.Ready;
                voiceInput.ErrorMessage = string.Empty;
                progress?.Report("Voice input is ready.");
            }
            catch (Exception ex)
            {
                DisposeFactory();
                voiceInput.Status = VoiceInputInitializationStatus.Error;
                voiceInput.ErrorMessage = BuildErrorMessage(ex);
                progress?.Report(voiceInput.ErrorMessage);
                _logger.LogError(ex, "Voice input initialization failed.");
            }

            await _userSettingsService.SaveSettingsAsync(userSettings, cancellationToken);
            return voiceInput;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioStream);

        var settings = await GetSettingsAsync(cancellationToken);
        if (settings.Status != VoiceInputInitializationStatus.Ready)
        {
            throw new InvalidOperationException("Voice input is not initialized.");
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var factory = await EnsureFactoryLoadedAsync(forceReload: false, progress: null, cancellationToken);
            if (audioStream.CanSeek)
            {
                audioStream.Position = 0;
            }

            using var processor = factory.CreateBuilder()
                .WithLanguage(settings.RecognitionLanguage)
                .Build();

            var transcription = new StringBuilder();
            try
            {
                await foreach (var segment in processor.ProcessAsync(audioStream))
                {
                    transcription.Append(segment.Text);
                }
            }
            catch (Exception ex) when (IsInvalidWaveFormat(ex))
            {
                throw new InvalidOperationException(
                    "Voice input received an unsupported audio format. Please try recording again.",
                    ex);
            }

            var text = transcription.ToString().Trim();
            return string.Equals(text, "[BLANK_AUDIO]", StringComparison.Ordinal)
                ? string.Empty
                : text;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void Dispose()
    {
        _operationLock.Dispose();
        DisposeFactory();
    }

    private async Task EnsureModelExistsAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var modelFilePath = GetModelFilePath();
        if (File.Exists(modelFilePath))
        {
            progress?.Report("Whisper model found.");
            return;
        }

        progress?.Report("Downloading Whisper model...");
        var tempFilePath = $"{modelFilePath}.download";
        if (File.Exists(tempFilePath))
        {
            File.Delete(tempFilePath);
        }

        await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(_modelType, cancellationToken: cancellationToken);
        await using (var fileStream = File.Create(tempFilePath))
        {
            await modelStream.CopyToAsync(fileStream, cancellationToken);
        }

        File.Move(tempFilePath, modelFilePath, overwrite: true);
        progress?.Report("Whisper model downloaded.");
    }

    private async Task<WhisperFactory> EnsureFactoryLoadedAsync(bool forceReload, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_factory is not null && !forceReload)
        {
            return _factory;
        }

        DisposeFactory();
        progress?.Report("Loading Whisper runtime...");
        var factory = WhisperFactory.FromPath(GetModelFilePath());
        using var processor = factory.CreateBuilder()
            .WithLanguage(_options.RecognitionLanguage)
            .Build();

        _factory = factory;
        progress?.Report("Whisper runtime loaded.");
        return factory;
    }

    private void DisposeFactory()
    {
        _factory?.Dispose();
        _factory = null;
    }

    private static void NormalizeVoiceInputSettings(VoiceInputSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.RecognitionLanguage))
        {
            settings.RecognitionLanguage = "auto";
        }
    }

    private string GetModelFilePath() =>
        Path.Combine(_voiceInputDirectoryPath, GetModelFileName(_modelType));

    private static GgmlType ResolveModelType(string? modelType)
    {
        if (Enum.TryParse<GgmlType>(modelType, ignoreCase: true, out var parsedType))
        {
            return parsedType;
        }

        return GgmlType.Base;
    }

    private static string GetModelFileName(GgmlType modelType) =>
        modelType switch
        {
            GgmlType.Tiny => "ggml-tiny.bin",
            GgmlType.TinyEn => "ggml-tiny.en.bin",
            GgmlType.Base => "ggml-base.bin",
            GgmlType.BaseEn => "ggml-base.en.bin",
            GgmlType.Small => "ggml-small.bin",
            GgmlType.SmallEn => "ggml-small.en.bin",
            GgmlType.Medium => "ggml-medium.bin",
            GgmlType.MediumEn => "ggml-medium.en.bin",
            GgmlType.LargeV1 => "ggml-large-v1.bin",
            GgmlType.LargeV2 => "ggml-large-v2.bin",
            GgmlType.LargeV3 => "ggml-large-v3.bin",
            GgmlType.LargeV3Turbo => "ggml-large-v3-turbo.bin",
            _ => "ggml-base.bin"
        };

    private static string BuildErrorMessage(Exception exception) =>
        exception switch
        {
            InvalidOperationException => exception.Message,
            DllNotFoundException => "Whisper runtime is unavailable on this system.",
            HttpRequestException => "Failed to download the Whisper model.",
            _ => "Voice input initialization failed."
        };

    private static bool IsInvalidWaveFormat(Exception exception) =>
        exception.GetType().FullName is string fullName &&
        fullName.StartsWith("Whisper.net.Wave.", StringComparison.Ordinal) &&
        fullName.EndsWith("WaveException", StringComparison.Ordinal);
}
