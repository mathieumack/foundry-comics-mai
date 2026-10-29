using Azure.Core;
using MaiImageGenerator.Api.Configuration;
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
        CancellationToken cancellationToken)
    {
        try
        {
            GeneratedImage image;
            if (referenceSheet is null)
            {
                return await GenerateFromPromptAsync(prompt, cancellationToken);
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
                return image.ImageBytes.ToArray();
            }

            if (image.ImageUri is not null)
            {
                return await httpClientFactory.CreateClient().GetByteArrayAsync(image.ImageUri, cancellationToken);
            }

            throw new FoundryGenerationException("The image model returned no image.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FoundryGenerationException("Image generation request timed out.", exception);
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
            throw new FoundryGenerationException(
                $"Image generation failed with HTTP status {exception.Status}.",
                exception,
                exception.Status);
        }
    }

    private async Task<byte[]> GenerateFromPromptAsync(
        string prompt,
        CancellationToken cancellationToken)
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
        if (!response.IsSuccessStatusCode)
        {
            throw new FoundryGenerationException(
                $"Image generation failed with HTTP status {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken);
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