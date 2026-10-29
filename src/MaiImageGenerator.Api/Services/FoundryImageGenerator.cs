using Azure.Core;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using MaiImageGenerator.Shared.Services;
using Microsoft.Extensions.Options;
using OpenAI.Images;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MaiImageGenerator.Api.Services;

public sealed class FoundryImageGenerator(
    ImageClient imageClient,
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    IOptions<FoundryOptions> options) : IImageGenerator
{
    internal const string HttpClientName = "MaiImageGeneration";
    private static readonly string[] CognitiveServicesScopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly FoundryOptions _options = options.Value;
    private readonly Uri _generationsEndpoint = GetGenerationsEndpoint(options.Value.Endpoint);
    private readonly string _imageModel = options.Value.ImageModel;
    private readonly int _squareImageSize = options.Value.ImageSize == 1024
        ? options.Value.ImageSize
        : throw new InvalidOperationException("MAI image generation supports ImageSize=1024 for square images.");
    private readonly GeneratedImageSize _imageSize = options.Value.ImageSize switch
    {
        1024 => GeneratedImageSize.W1024xH1024,
        _ => throw new InvalidOperationException("The image SDK supports ImageSize=1024 for square images.")
    };

    public async Task<byte[]> GenerateAsync(
        string prompt,
        byte[]? referenceSheet,
        CancellationToken cancellationToken,
        AiGenerationTrace? trace = null)
    {
        trace ??= new AiGenerationTrace();
        using var capture = AiTraceCapture.Begin(
            trace, "image-generation", _imageModel, $"{_squareImageSize}x{_squareImageSize}");
        try
        {
            if (referenceSheet is null)
            {
                var bytes = await GenerateFromPromptAsync(prompt, cancellationToken, trace);
                capture.Complete();
                return bytes;
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
                AiTraceCapture.CaptureRequestId(trace, name =>
                    result.GetRawResponse().Headers.TryGetValue(name, out var value) ? value : null);
                AiTraceCapture.CaptureImageResponse(trace, () =>
                    JsonDocument.Parse(result.GetRawResponse().Content.ToMemory()));
                using var document = JsonDocument.Parse(result.GetRawResponse().Content.ToMemory());
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                {
                    throw new FoundryGenerationException("The image model returned no image.");
                }

                var image = data[0];
                byte[] bytes;
                if (image.TryGetProperty("b64_json", out var encoded) && encoded.ValueKind == JsonValueKind.String &&
                    encoded.GetString() is { } base64)
                {
                    bytes = Convert.FromBase64String(base64);
                }
                else if (image.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
                    url.GetString() is { } imageUrl)
                {
                    bytes = await httpClientFactory.CreateClient().GetByteArrayAsync(new Uri(imageUrl), cancellationToken);
                }
                else
                {
                    throw new FoundryGenerationException("The image model returned no image.");
                }

                capture.Complete();
                return bytes;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FoundryGenerationException("Image generation request timed out.", exception);
        }
        catch (OperationCanceledException)
        {
            capture.Cancel();
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new FoundryGenerationException("Image generation request failed.", exception);
        }
        catch (JsonException exception)
        {
            throw new FoundryGenerationException("The image model returned an invalid response.", exception);
        }
        catch (FormatException exception)
        {
            throw new FoundryGenerationException("The image model returned invalid image data.", exception);
        }
        catch (ClientResultException exception)
        {
            AiTraceCapture.CaptureRequestId(trace, name =>
            {
                var response = exception.GetRawResponse();
                return response is not null && response.Headers.TryGetValue(name, out var value) ? value : null;
            });
            AiTraceCapture.CaptureImageResponse(trace, () =>
                JsonDocument.Parse(exception.GetRawResponse()!.Content.ToMemory()));
            throw new FoundryGenerationException(
                $"Image generation failed with HTTP status {exception.Status}.",
                exception,
                exception.Status)
            {
                RetryAfter = ImageGenerationRetry.ReadRetryAfter(name =>
                    exception.GetRawResponse() is { } response &&
                    response.Headers.TryGetValue(name, out var value) ? value : null)
            };
        }
    }

    private async Task<byte[]> GenerateFromPromptAsync(
        string prompt,
        CancellationToken cancellationToken,
        AiGenerationTrace trace)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(CognitiveServicesScopes),
            cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, _generationsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var requestBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = _options.ImageModel,
            prompt,
            width = _squareImageSize,
            height = _squareImageSize
        });
        request.Content = new ByteArrayContent(requestBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentLength = requestBody.Length;

        using var response = await httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(request, cancellationToken);
        AiTraceCapture.CaptureRequestId(trace, name =>
            response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        AiTraceCapture.CaptureImageResponse(trace, () => JsonDocument.Parse(responseBody));
        if (!response.IsSuccessStatusCode)
        {
            throw new FoundryGenerationException(
                $"Image generation failed with HTTP status {(int)response.StatusCode}.",
                (int)response.StatusCode)
            {
                RetryAfter = ImageGenerationRetry.ReadRetryAfter(name =>
                    response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null)
            };
        }

        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("b64_json", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { } imageBase64)
                {
                    return Convert.FromBase64String(imageBase64);
                }
            }
        }

        throw new FoundryGenerationException("The image model returned no image.");
    }

    private static Uri GetGenerationsEndpoint(string endpoint)
    {
        var builder = new UriBuilder(endpoint);
        const string cognitiveServicesSuffix = ".cognitiveservices.azure.com";
        if (builder.Host.EndsWith(cognitiveServicesSuffix, StringComparison.OrdinalIgnoreCase))
        {
            builder.Host = $"{builder.Host[..^cognitiveServicesSuffix.Length]}.services.ai.azure.com";
        }

        builder.Path = "/mai/v1/images/generations";
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        return builder.Uri;
    }
}