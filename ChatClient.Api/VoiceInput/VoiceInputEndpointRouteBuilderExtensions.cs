using ChatClient.Application.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ChatClient.Api.VoiceInput;

internal static class VoiceInputEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapVoiceInputEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/voice-input/transcribe",
                async Task<Results<Ok<VoiceInputTranscriptionResponse>, BadRequest<VoiceInputErrorResponse>, StatusCodeHttpResult>> (
                    [FromForm] IFormFile? audio,
                    [FromForm] string? operationId,
                    IVoiceInputService voiceInputService,
                    IOptions<VoiceInputOptions> optionsAccessor,
                    ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    var options = optionsAccessor.Value;

                    if (audio is null || audio.Length == 0)
                    {
                        return TypedResults.BadRequest(new VoiceInputErrorResponse("Audio payload is empty."));
                    }

                    if (audio.Length > options.MaxAudioBytes)
                    {
                        return TypedResults.BadRequest(new VoiceInputErrorResponse("Audio payload is too large."));
                    }

                    try
                    {
                        await using var audioStream = audio.OpenReadStream();
                        var text = await voiceInputService.TranscribeAsync(audioStream, operationId, cancellationToken);
                        return TypedResults.Ok(new VoiceInputTranscriptionResponse(text));
                    }
                    catch (InvalidOperationException ex)
                    {
                        return TypedResults.BadRequest(new VoiceInputErrorResponse(ex.Message));
                    }
                    catch (Exception ex)
                    {
                        loggerFactory.CreateLogger("VoiceInput")
                            .LogError(ex, "Voice transcription failed.");
                        return TypedResults.StatusCode(StatusCodes.Status500InternalServerError);
                    }
                })
            .DisableAntiforgery();

        return endpoints;
    }

    private sealed record VoiceInputTranscriptionResponse(string Text);

    private sealed record VoiceInputErrorResponse(string Message);
}
