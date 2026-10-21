using System.Diagnostics;
using MaiImageGenerator.Api.Configuration;
using Microsoft.Extensions.Options;
using OpenAI.Images;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;

namespace MaiImageGenerator.Api.Services;

public sealed class FoundryImageGenerator(
    ImageClient imageClient,
    IHttpClientFactory httpClientFactory,
    IOptions<FoundryOptions> options,
    ILogger<FoundryImageGenerator> logger) : IImageGenerator
{
    private readonly string _imageModel = options.Value.ImageModel;
    private readonly string _upstreamEndpoint =
        new Uri(options.Value.Endpoint).GetLeftPart(UriPartial.Authority);
    private readonly GeneratedImageSize _imageSize = options.Value.ImageSize switch
    {
        1024 => GeneratedImageSize.W1024xH1024,
        _ => throw new InvalidOperationException("The image SDK supports ImageSize=1024 for square images.")
    };

    public async Task<byte[]> GenerateAsync(
        string prompt,
        byte[]? referenceSheet,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "AI operation {OperationName} started for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint}.",
            "GenerateImage",
            "AzureFoundry",
            _imageModel,
            "ImageGeneration",
            _upstreamEndpoint);
        try
        {
            GeneratedImage image;
            if (referenceSheet is null)
            {
                var result = await imageClient.GenerateImageAsync(
                    prompt,
                    new ImageGenerationOptions
                    {
                        Size = _imageSize
                    },
                    cancellationToken);
                image = result.Value;
            }
            else
            {
                await using var imageStream = new MemoryStream(referenceSheet, writable: false);
                using var multipart = new MultipartFormDataContent();
                var imageContent = new StreamContent(imageStream);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                multipart.Add(imageContent, "image", "character-references.png");
                multipart.Add(new StringContent(prompt), "prompt");
                multipart.Add(new StringContent(_imageModel), "model");
                multipart.Add(new StringContent(_imageSize.ToString()), "size");

                await using var requestStream = new MemoryStream();
                await multipart.CopyToAsync(requestStream, cancellationToken);
                requestStream.Position = 0;
                using var content = BinaryContent.Create(requestStream);

                var result = await imageClient.GenerateImageEditsAsync(
                    content,
                    multipart.Headers.ContentType!.ToString(),
                    new RequestOptions
                    {
                        CancellationToken = cancellationToken
                    });
#pragma warning disable OPENAI001
                image = ((GeneratedImageCollection)result).FirstOrDefault()
                    ?? throw new FoundryGenerationException("The image model returned no image.");
#pragma warning restore OPENAI001
            }

            if (image.ImageBytes is not null)
            {
                logger.LogInformation(
                    "AI operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                    "GenerateImage",
                    "Succeeded",
                    stopwatch.ElapsedMilliseconds);
                return image.ImageBytes.ToArray();
            }

            if (image.ImageUri is not null)
            {
                byte[] bytes;
                try
                {
                    bytes = await httpClientFactory.CreateClient()
                        .GetByteArrayAsync(image.ImageUri, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    throw new FoundryGenerationException("The generated image could not be downloaded.");
                }

                logger.LogInformation(
                    "AI operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                    "GenerateImage",
                    "Succeeded",
                    stopwatch.ElapsedMilliseconds);
                return bytes;
            }

            throw new FoundryGenerationException("The image model returned no image.");
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "AI operation {OperationName} ended with outcome {Outcome} for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint} after {ElapsedMs} ms.",
                "GenerateImage",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                "AzureFoundry",
                _imageModel,
                "ImageGeneration",
                _upstreamEndpoint,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (ClientResultException exception)
        {
            var safeException = new FoundryGenerationException(
                $"Image generation failed with HTTP status {exception.Status}.");
            logger.LogError(
                safeException,
                "AI operation {OperationName} failed for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint} with status {UpstreamStatus}, request {ProviderRequestId}, and code {SafeErrorCode} after {ElapsedMs} ms.",
                "GenerateImage",
                "AzureFoundry",
                _imageModel,
                "ImageGeneration",
                _upstreamEndpoint,
                exception.Status,
                GetProviderRequestId(exception),
                "ClientResult",
                stopwatch.ElapsedMilliseconds);
            throw safeException;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "AI operation {OperationName} failed for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint} with status {UpstreamStatus}, request {ProviderRequestId}, and code {SafeErrorCode} after {ElapsedMs} ms.",
                "GenerateImage",
                "AzureFoundry",
                _imageModel,
                "ImageGeneration",
                _upstreamEndpoint,
                null,
                null,
                exception.GetType().Name,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static string? GetProviderRequestId(ClientResultException exception)
    {
        var response = exception.GetRawResponse();
        foreach (var headerName in new[] { "x-ms-request-id", "apim-request-id", "request-id" })
        {
            if (response?.Headers.TryGetValue(headerName, out var value) == true)
            {
                return SanitizeTelemetryValue(value);
            }
        }

        return null;
    }

    private static string? SanitizeTelemetryValue(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            ? value
            : null;
}