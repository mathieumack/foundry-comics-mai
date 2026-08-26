using MaiImageGenerator.Api.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAI.Images;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class FoundryImageClient(
    AIAgent promptAgent,
    ImageClient imageClient,
    HttpClient httpClient,
    IOptions<FoundryOptions> options)
{
    internal const string RewriteInstructions = """
        You are an art director specializing in image-generation prompts. Rewrite the supplied context and panel
        description into one autonomous prompt. Preserve the requested characters, action, framing, visual style,
        displayed text, dialogue, and constraints. Return only the final prompt without Markdown or commentary.
        """;

    private readonly FoundryOptions _options = options.Value;
    private readonly GeneratedImageSize _imageSize = options.Value.ImageSize switch
    {
        1024 => GeneratedImageSize.W1024xH1024,
        _ => throw new InvalidOperationException("The image SDK supports ImageSize=1024 for square images.")
    };

    public async Task<string> RewritePromptAsync(
        string? context,
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await promptAgent.RunAsync(
                $"Global context:\n{context ?? "None"}\n\nPanel prompt:\n{prompt}",
                cancellationToken: cancellationToken);
            var rewrittenPrompt = response.ToString().Trim();
            return string.IsNullOrWhiteSpace(rewrittenPrompt)
                ? throw new FoundryGenerationException("The prompt model returned no content.")
                : rewrittenPrompt;
        }
        catch (ClientResultException exception)
        {
            throw new FoundryGenerationException(
                $"Prompt rewriting failed with HTTP status {exception.Status}.",
                exception);
        }
    }

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
                    new ImageGenerationOptions { Size = _imageSize },
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
                multipart.Add(new StringContent(_options.ImageModel), "model");
                multipart.Add(new StringContent(_imageSize.ToString()), "size");

                await using var requestStream = new MemoryStream();
                await multipart.CopyToAsync(requestStream, cancellationToken);
                requestStream.Position = 0;
                using var content = BinaryContent.Create(requestStream);
                var result = await imageClient.GenerateImageEditsAsync(
                    content,
                    multipart.Headers.ContentType!.ToString(),
                    new RequestOptions { CancellationToken = cancellationToken });
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
                return await httpClient.GetByteArrayAsync(image.ImageUri, cancellationToken);
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

public sealed class FoundryGenerationException : Exception
{
    public FoundryGenerationException(string message) : base(message)
    {
    }

    public FoundryGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}