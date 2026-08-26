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
    IOptions<FoundryOptions> options) : IImageGenerator
{
    private readonly string _imageModel = options.Value.ImageModel;
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
                return image.ImageBytes.ToArray();
            }

            if (image.ImageUri is not null)
            {
                return await httpClientFactory.CreateClient().GetByteArrayAsync(image.ImageUri, cancellationToken);
            }

            throw new FoundryGenerationException("The image model returned no image.");
        }
        catch (ClientResultException exception)
        {
            throw new FoundryGenerationException(
                $"Image generation failed with HTTP status {exception.Status}.",
                exception);
        }
    }
}