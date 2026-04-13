using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace ChatClient.Api.VoiceInput;

public sealed class VoiceInputService(
    IConfiguration configuration,
    IOptions<VoiceInputOptions> optionsAccessor,
    IUserSettingsService userSettingsService,
    ILogger<VoiceInputService> logger) : IVoiceInputService, IDisposable
{
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<GgmlType> SupportedModelTypes =
    [
        GgmlType.Tiny,
        GgmlType.Base,
        GgmlType.Small,
        GgmlType.Medium,
        GgmlType.LargeV3Turbo,
        GgmlType.LargeV3,
        GgmlType.TinyEn,
        GgmlType.BaseEn,
        GgmlType.SmallEn,
        GgmlType.MediumEn,
        GgmlType.LargeV2,
        GgmlType.LargeV1
    ];

    private readonly IUserSettingsService _userSettingsService = userSettingsService;
    private readonly ILogger<VoiceInputService> _logger = logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly VoiceInputOptions _options = optionsAccessor.Value;
    private readonly string _voiceInputDirectoryPath = StoragePathResolver.ResolveUserPath(
        configuration,
        optionsAccessor.Value.DirectoryPath,
        FilePathConstants.DefaultVoiceInputDirectory);
    private readonly ConcurrentDictionary<string, ProgressSnapshot> _transcriptionProgress = new(StringComparer.Ordinal);

    private WhisperFactory? _factory;
    private string? _loadedModelFilePath;

    public async Task<VoiceInputSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var userSettings = await _userSettingsService.GetSettingsAsync(cancellationToken);
        var voiceInput = userSettings.VoiceInput ??= new VoiceInputSettings();
        var updated = NormalizeVoiceInputSettings(voiceInput);
        var modelFilePath = GetModelFilePath(voiceInput);

        if (voiceInput.Status == VoiceInputInitializationStatus.Initializing)
        {
            voiceInput.Status = VoiceInputInitializationStatus.NotInitialized;
            voiceInput.ErrorMessage = string.Empty;
            updated = true;
        }

        if (voiceInput.Status == VoiceInputInitializationStatus.Ready && !File.Exists(modelFilePath))
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
                await EnsureModelExistsAsync(voiceInput, progress, cancellationToken);
                await EnsureFactoryLoadedAsync(voiceInput, forceReload: true, progress, cancellationToken);

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

    public Task<VoiceInputTranscriptionProgress?> GetTranscriptionProgressAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpiredProgress();

        if (string.IsNullOrWhiteSpace(operationId) ||
            !_transcriptionProgress.TryGetValue(operationId, out var snapshot))
        {
            return Task.FromResult<VoiceInputTranscriptionProgress?>(null);
        }

        return Task.FromResult<VoiceInputTranscriptionProgress?>(
            new VoiceInputTranscriptionProgress
            {
                ProgressPercent = snapshot.ProgressPercent,
                IsCompleted = snapshot.IsCompleted
            });
    }

    public async Task<VoiceInputStorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(_voiceInputDirectoryPath))
            {
                return new VoiceInputStorageInfo();
            }

            var downloadedModels = Directory.EnumerateFiles(_voiceInputDirectoryPath, "ggml-*.bin", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var fileInfo = new FileInfo(path);
                    var modelType = ResolveModelTypeByFileName(fileInfo.Name);
                    var displayName = modelType is null
                        ? fileInfo.Name
                        : GetModelDisplayName(modelType.Value);

                    return new VoiceInputDownloadedModel(
                        modelType is null ? fileInfo.Name : GetModelTypeName(modelType.Value),
                        displayName,
                        fileInfo.Name,
                        fileInfo.Length);
                })
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new VoiceInputStorageInfo
            {
                TotalBytes = downloadedModels.Sum(model => model.SizeBytes),
                DownloadedModels = downloadedModels
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<VoiceInputSettings> ClearDownloadedModelsAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            DisposeFactory();

            if (Directory.Exists(_voiceInputDirectoryPath))
            {
                foreach (var filePath in Directory.EnumerateFiles(_voiceInputDirectoryPath, "ggml-*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(filePath);
                }
            }

            var userSettings = await _userSettingsService.GetSettingsAsync(cancellationToken);
            var voiceInput = userSettings.VoiceInput ??= new VoiceInputSettings();
            NormalizeVoiceInputSettings(voiceInput);
            voiceInput.Status = VoiceInputInitializationStatus.NotInitialized;
            voiceInput.ErrorMessage = string.Empty;
            await _userSettingsService.SaveSettingsAsync(userSettings, cancellationToken);
            return voiceInput;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(
        Stream audioStream,
        string? operationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioStream);
        var normalizedOperationId = NormalizeOperationId(operationId);

        var settings = await GetSettingsAsync(cancellationToken);
        if (settings.Status != VoiceInputInitializationStatus.Ready)
        {
            throw new InvalidOperationException("Voice input is not initialized.");
        }

        StartProgressTracking(normalizedOperationId);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var factory = await EnsureFactoryLoadedAsync(settings, forceReload: false, progress: null, cancellationToken);
            if (audioStream.CanSeek)
            {
                audioStream.Position = 0;
            }

            var builder = factory.CreateBuilder()
                .WithLanguage(settings.RecognitionLanguage);

            if (normalizedOperationId is not null)
            {
                builder = builder.WithProgressHandler(progress => UpdateProgressTracking(normalizedOperationId, progress));
            }

            using var processor = builder.Build();

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
            CompleteProgressTracking(normalizedOperationId);
            return string.Equals(text, "[BLANK_AUDIO]", StringComparison.Ordinal)
                ? string.Empty
                : text;
        }
        catch
        {
            CompleteProgressTracking(normalizedOperationId);
            throw;
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

    private async Task EnsureModelExistsAsync(VoiceInputSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var modelType = ResolveConfiguredModelType(settings.ModelType);
        var modelFilePath = GetModelFilePath(settings);
        if (File.Exists(modelFilePath))
        {
            progress?.Report($"Whisper {GetModelTypeName(modelType)} model found.");
            return;
        }

        progress?.Report($"Downloading Whisper {GetModelTypeName(modelType)} model...");
        var tempFilePath = $"{modelFilePath}.download";
        if (File.Exists(tempFilePath))
        {
            File.Delete(tempFilePath);
        }

        await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelType, cancellationToken: cancellationToken);
        await using (var fileStream = File.Create(tempFilePath))
        {
            await modelStream.CopyToAsync(fileStream, cancellationToken);
        }

        File.Move(tempFilePath, modelFilePath, overwrite: true);
        progress?.Report($"Whisper {GetModelTypeName(modelType)} model downloaded.");
    }

    private async Task<WhisperFactory> EnsureFactoryLoadedAsync(
        VoiceInputSettings settings,
        bool forceReload,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelFilePath = GetModelFilePath(settings);

        if (_factory is not null &&
            !forceReload &&
            string.Equals(_loadedModelFilePath, modelFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return _factory;
        }

        DisposeFactory();
        progress?.Report("Loading Whisper runtime...");
        var factory = WhisperFactory.FromPath(modelFilePath);
        using var processor = factory.CreateBuilder()
            .WithLanguage(settings.RecognitionLanguage)
            .Build();

        _factory = factory;
        _loadedModelFilePath = modelFilePath;
        progress?.Report("Whisper runtime loaded.");
        return factory;
    }

    private void DisposeFactory()
    {
        _factory?.Dispose();
        _factory = null;
        _loadedModelFilePath = null;
    }

    private bool NormalizeVoiceInputSettings(VoiceInputSettings settings)
    {
        var updated = false;

        var recognitionLanguage = string.IsNullOrWhiteSpace(settings.RecognitionLanguage)
            ? _options.RecognitionLanguage
            : settings.RecognitionLanguage;
        if (string.IsNullOrWhiteSpace(recognitionLanguage))
        {
            recognitionLanguage = "auto";
        }

        if (!string.Equals(settings.RecognitionLanguage, recognitionLanguage, StringComparison.Ordinal))
        {
            settings.RecognitionLanguage = recognitionLanguage;
            updated = true;
        }

        var modelType = GetModelTypeName(ResolveConfiguredModelType(settings.ModelType));
        if (!string.Equals(settings.ModelType, modelType, StringComparison.Ordinal))
        {
            settings.ModelType = modelType;
            updated = true;
        }

        if (settings.ErrorMessage is null)
        {
            settings.ErrorMessage = string.Empty;
            updated = true;
        }

        return updated;
    }

    private string GetModelFilePath(VoiceInputSettings settings) =>
        Path.Combine(_voiceInputDirectoryPath, GetModelFileName(ResolveConfiguredModelType(settings.ModelType)));

    private GgmlType ResolveConfiguredModelType(string? modelType)
    {
        if (Enum.TryParse<GgmlType>(modelType, ignoreCase: true, out var parsedType))
        {
            return parsedType;
        }

        return ResolveModelType(_options.ModelType);
    }

    private static string GetModelTypeName(GgmlType modelType) =>
        modelType switch
        {
            GgmlType.Tiny => nameof(GgmlType.Tiny),
            GgmlType.TinyEn => nameof(GgmlType.TinyEn),
            GgmlType.Base => nameof(GgmlType.Base),
            GgmlType.BaseEn => nameof(GgmlType.BaseEn),
            GgmlType.Small => nameof(GgmlType.Small),
            GgmlType.SmallEn => nameof(GgmlType.SmallEn),
            GgmlType.Medium => nameof(GgmlType.Medium),
            GgmlType.MediumEn => nameof(GgmlType.MediumEn),
            GgmlType.LargeV1 => nameof(GgmlType.LargeV1),
            GgmlType.LargeV2 => nameof(GgmlType.LargeV2),
            GgmlType.LargeV3 => nameof(GgmlType.LargeV3),
            GgmlType.LargeV3Turbo => nameof(GgmlType.LargeV3Turbo),
            _ => nameof(GgmlType.Base)
        };

    private static string GetModelDisplayName(GgmlType modelType) =>
        modelType switch
        {
            GgmlType.LargeV3Turbo => "Large V3 Turbo",
            GgmlType.TinyEn => "Tiny (English only)",
            GgmlType.BaseEn => "Base (English only)",
            GgmlType.SmallEn => "Small (English only)",
            GgmlType.MediumEn => "Medium (English only)",
            GgmlType.LargeV1 => "Large V1",
            GgmlType.LargeV2 => "Large V2",
            GgmlType.LargeV3 => "Large V3",
            _ => GetModelTypeName(modelType)
        };

    private static GgmlType ResolveModelType(string? modelType)
    {
        if (Enum.TryParse<GgmlType>(modelType, ignoreCase: true, out var parsedType))
        {
            return parsedType;
        }

        return GgmlType.Base;
    }

    private static GgmlType? ResolveModelTypeByFileName(string fileName)
    {
        foreach (var modelType in SupportedModelTypes)
        {
            if (string.Equals(GetModelFileName(modelType), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return modelType;
            }
        }

        return null;
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

    private void StartProgressTracking(string? operationId)
    {
        if (operationId is null)
        {
            return;
        }

        CleanupExpiredProgress();
        _transcriptionProgress[operationId] = new ProgressSnapshot(0, false, DateTimeOffset.UtcNow);
    }

    private void UpdateProgressTracking(string operationId, int progressPercent)
    {
        var normalizedProgress = Math.Clamp(progressPercent, 0, 100);
        var timestamp = DateTimeOffset.UtcNow;

        _transcriptionProgress.AddOrUpdate(
            operationId,
            static (key, arg) => new ProgressSnapshot(arg.ProgressPercent, false, arg.Timestamp),
            static (key, existing, arg) => new ProgressSnapshot(
                Math.Max(existing.ProgressPercent, arg.ProgressPercent),
                existing.IsCompleted,
                arg.Timestamp),
            (ProgressPercent: normalizedProgress, Timestamp: timestamp));
    }

    private void CompleteProgressTracking(string? operationId)
    {
        if (operationId is null)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        _transcriptionProgress.AddOrUpdate(
            operationId,
            static (key, arg) => new ProgressSnapshot(100, true, arg),
            static (key, existing, arg) => new ProgressSnapshot(
                Math.Max(existing.ProgressPercent, 100),
                true,
                arg),
            timestamp);
    }

    private void CleanupExpiredProgress()
    {
        var expirationThreshold = DateTimeOffset.UtcNow - ProgressRetention;
        foreach (var pair in _transcriptionProgress)
        {
            if (pair.Value.UpdatedAtUtc < expirationThreshold)
            {
                _transcriptionProgress.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string? NormalizeOperationId(string? operationId) =>
        string.IsNullOrWhiteSpace(operationId)
            ? null
            : operationId.Trim();

    private sealed record ProgressSnapshot(int ProgressPercent, bool IsCompleted, DateTimeOffset UpdatedAtUtc);
}
