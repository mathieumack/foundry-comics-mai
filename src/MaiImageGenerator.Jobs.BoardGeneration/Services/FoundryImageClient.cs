using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Images;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class FoundryImageClient(
    OpenAIClient openAIClient,
    ImageClient imageClient,
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<FoundryOptions> options)
{
    private const int LandscapeWidth = 1365;
    private const int LandscapeHeight = 768;
    private static readonly string[] CognitiveServicesScopes = ["https://cognitiveservices.azure.com/.default"];
    internal const string RewriteInstructions = """
        You are an art director specializing in image-generation prompts. Rewrite the supplied context and panel
        description into one autonomous prompt. Preserve the requested characters, action, framing, visual style,
        displayed text, dialogue, and constraints. Return only the final prompt without Markdown or commentary.
        """;

    private readonly FoundryOptions _options = options.Value;
    private readonly Uri _generationsEndpoint = GetGenerationsEndpoint(options.Value.Endpoint);
    private readonly int _squareImageSize = options.Value.ImageSize == 1024
        ? options.Value.ImageSize
        : throw new InvalidOperationException("MAI image generation supports ImageSize=1024 for square panels.");

    public async Task<string> RewritePromptAsync(
        string? context,
        string prompt,
        CancellationToken cancellationToken,
        AiGenerationTrace? trace = null)
    {
        trace ??= new AiGenerationTrace();
        using var capture = AiTraceCapture.Begin(trace, "prompt-rewrite", _options.PromptModel);
        try
        {
            var promptAgent = new FoundryChatTraceClient(openAIClient.GetChatClient(_options.PromptModel), trace)
                .AsAIAgent(instructions: RewriteInstructions, name: "PromptRewriter");
            var response = await promptAgent.RunAsync(
                $"Global context:\n{context ?? "None"}\n\nPanel prompt:\n{prompt}",
                cancellationToken: cancellationToken);
            var rewrittenPrompt = response.ToString().Trim();
            if (string.IsNullOrWhiteSpace(rewrittenPrompt))
            {
                throw new FoundryGenerationException("The prompt model returned no content.");
            }

            capture.Complete();
            return rewrittenPrompt;
        }
        catch (OperationCanceledException)
        {
            capture.Cancel();
            throw;
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
        bool landscape,
        CancellationToken cancellationToken,
        AiGenerationTrace? trace = null)
    {
        trace ??= new AiGenerationTrace();
        var (width, height) = GetImageDimensions(landscape);
        using var capture = AiTraceCapture.Begin(trace, "panel-image", _options.ImageModel, $"{width}x{height}");
        try
        {
            if (referenceSheet is null)
            {
                var bytes = await GenerateFromPromptAsync(prompt, width, height, cancellationToken, trace);
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
                multipart.Add(new StringContent(_options.ImageModel), "model");
                multipart.Add(new StringContent($"{width}x{height}"), "size");

                await using var requestStream = new MemoryStream();
                await multipart.CopyToAsync(requestStream, cancellationToken);
                requestStream.Position = 0;
                using var content = BinaryContent.Create(requestStream);
                var result = await imageClient.GenerateImageEditsAsync(
                    content,
                    multipart.Headers.ContentType!.ToString(),
                    new RequestOptions { CancellationToken = cancellationToken });
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
                    bytes = await httpClient.GetByteArrayAsync(new Uri(imageUrl), cancellationToken);
                }
                else
                {
                    throw new FoundryGenerationException("The image model returned no image.");
                }

                capture.Complete();
                return bytes;
            }
        }
        catch (OperationCanceledException)
        {
            capture.Cancel();
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new FoundryGenerationException(
                "Image generation request failed.",
                exception,
                exception.StatusCode is null ? null : (int)exception.StatusCode);
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
            if (exception.Status == 400 &&
                exception.Message.Contains("content_safety_violation", StringComparison.OrdinalIgnoreCase))
            {
                throw new FoundryContentSafetyException(
                    "The image was not generated because its prompt or references were blocked by the content safety policy.",
                    exception);
            }

            throw new FoundryGenerationException(
                $"Image generation failed with HTTP status {exception.Status}.",
                exception,
                exception.Status);
        }
    }

    private async Task<byte[]> GenerateFromPromptAsync(
        string prompt,
        int width,
        int height,
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
            width,
            height
        });
        request.Content = new ByteArrayContent(requestBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentLength = requestBody.Length;
        using var response = await httpClient.SendAsync(request, cancellationToken);
        AiTraceCapture.CaptureRequestId(trace, name =>
            response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        AiTraceCapture.CaptureImageResponse(trace, () => JsonDocument.Parse(responseBody));
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 400 &&
                responseBody.Contains("content_safety_violation", StringComparison.OrdinalIgnoreCase))
            {
                throw new FoundryContentSafetyException(
                    "The image was not generated because its prompt was blocked by the content safety policy.");
            }

            throw new FoundryGenerationException(
                $"Image generation failed with HTTP status {(int)response.StatusCode}.",
                status: (int)response.StatusCode);
        }

        using var document = JsonDocument.Parse(responseBody);
        var imageBase64 = document.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(item => item.TryGetProperty("b64_json", out var value) ? value.GetString() : null)
            .FirstOrDefault(value => value is not null)
            ?? throw new FoundryGenerationException("The image model returned no image.");
        return Convert.FromBase64String(imageBase64);
    }

    private (int Width, int Height) GetImageDimensions(bool landscape) =>
        landscape
            ? (LandscapeWidth, LandscapeHeight)
            : (_squareImageSize, _squareImageSize);

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
        return builder.Uri;
    }

}

public class FoundryGenerationException : Exception
{
    public int? Status { get; }

    public FoundryGenerationException(string message) : base(message)
    {
    }

    public FoundryGenerationException(string message, int status) : base(message)
    {
        Status = status;
    }

    public FoundryGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FoundryGenerationException(string message, Exception innerException, int? status)
        : base(message, innerException)
    {
        Status = status;
    }
}

public sealed class FoundryContentSafetyException : FoundryGenerationException
{
    public FoundryContentSafetyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FoundryContentSafetyException(string message)
        : base(message)
    {
    }
}