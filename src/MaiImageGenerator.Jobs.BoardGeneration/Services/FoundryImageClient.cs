using System.Diagnostics;
using MaiImageGenerator.Api.Configuration;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Images;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class FoundryImageClient(
    AIAgent promptAgent,
    ImageClient imageClient,
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<FoundryOptions> options,
    ILogger<FoundryImageClient> logger)
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
    private readonly string _upstreamEndpoint =
        new Uri(options.Value.Endpoint).GetLeftPart(UriPartial.Authority);
    private readonly int _squareImageSize = options.Value.ImageSize == 1024
        ? options.Value.ImageSize
        : throw new InvalidOperationException("MAI image generation supports ImageSize=1024 for square panels.");

    public async Task<string> RewritePromptAsync(
        string? context,
        string prompt,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "AI operation {OperationName} started for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint}.",
            "RewritePrompt",
            "AzureFoundry",
            _options.PromptModel,
            "PromptRewrite",
            _upstreamEndpoint);
        try
        {
            var response = await promptAgent.RunAsync(
                $"Global context:\n{context ?? "None"}\n\nPanel prompt:\n{prompt}",
                cancellationToken: cancellationToken);
            var rewrittenPrompt = response.ToString().Trim();
            var result = string.IsNullOrWhiteSpace(rewrittenPrompt)
                ? throw new FoundryGenerationException("The prompt model returned no content.")
                : rewrittenPrompt;
            logger.LogInformation(
                "AI operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                "RewritePrompt",
                "Succeeded",
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "AI operation {OperationName} ended with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                "RewritePrompt",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                "PromptRewrite",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (ClientResultException exception)
        {
            var safeException = new FoundryGenerationException(
                $"Prompt rewriting failed with HTTP status {exception.Status}.",
                exception.Status,
                GetProviderRequestId(exception),
                "ClientResult");
            logger.LogError(
                safeException,
                "AI operation {OperationName} failed for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint} with status {UpstreamStatus} and code {SafeErrorCode} after {ElapsedMs} ms.",
                "RewritePrompt",
                "AzureFoundry",
                _options.PromptModel,
                "PromptRewrite",
                _upstreamEndpoint,
                exception.Status,
                "ClientResult",
                stopwatch.ElapsedMilliseconds);
            throw safeException;
        }
    }

    public async Task<byte[]> GenerateAsync(
        string prompt,
        byte[]? referenceSheet,
        bool landscape,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "AI operation {OperationName} started for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint}.",
            "GenerateImage",
            "AzureFoundry",
            _options.ImageModel,
            "ImageGeneration",
            _upstreamEndpoint);
        try
        {
            var (width, height) = GetImageDimensions(landscape);
            GeneratedImage image;
            if (referenceSheet is null)
            {
                var generated = await GenerateFromPromptAsync(prompt, width, height, cancellationToken);
                LogCompleted(stopwatch);
                return generated;
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
#pragma warning disable OPENAI001
                image = ((GeneratedImageCollection)result).FirstOrDefault()
                    ?? throw new FoundryGenerationException("The image model returned no image.");
#pragma warning restore OPENAI001
            }

            if (image.ImageBytes is not null)
            {
                LogCompleted(stopwatch);
                return image.ImageBytes.ToArray();
            }

            if (image.ImageUri is not null)
            {
                byte[] bytes;
                try
                {
                    bytes = await httpClient.GetByteArrayAsync(image.ImageUri, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    throw new FoundryGenerationException("The generated image could not be downloaded.");
                }

                LogCompleted(stopwatch);
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
                _options.ImageModel,
                "ImageGeneration",
                _upstreamEndpoint,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (HttpRequestException exception)
        {
            LogFailure(exception, stopwatch, null, null, "HttpRequest");
            throw new FoundryGenerationException("Image generation request failed.", exception);
        }
        catch (ClientResultException exception) when (
            exception.Status == 400 &&
            exception.Message.Contains("content_safety_violation", StringComparison.OrdinalIgnoreCase))
        {
            var safeException = new FoundryContentSafetyException(
                "The image was not generated because its prompt or references were blocked by the content safety policy.",
                exception.Status,
                GetProviderRequestId(exception),
                "content_safety_violation");
            logger.LogWarning(
                safeException,
                "AI operation {OperationName} completed with outcome {Outcome} for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint}, status {UpstreamStatus}, request {ProviderRequestId}, and code {SafeErrorCode} after {ElapsedMs} ms.",
                "GenerateImage",
                "ContentSafetyRejected",
                "AzureFoundry",
                _options.ImageModel,
                "ImageGeneration",
                _upstreamEndpoint,
                exception.Status,
                GetProviderRequestId(exception),
                "content_safety_violation",
                stopwatch.ElapsedMilliseconds);
            throw safeException;
        }
        catch (ClientResultException exception)
        {
            var requestId = GetProviderRequestId(exception);
            var safeException = new FoundryGenerationException(
                $"Image generation failed with HTTP status {exception.Status}.",
                exception.Status,
                requestId,
                "ClientResult");
            LogFailure(safeException, stopwatch, exception.Status, requestId, "ClientResult");
            throw safeException;
        }
        catch (FoundryContentSafetyException exception)
        {
            logger.LogWarning(
                exception,
                "AI operation {OperationName} completed with outcome {Outcome} for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint}, status {UpstreamStatus}, request {ProviderRequestId}, and code {SafeErrorCode} after {ElapsedMs} ms.",
                "GenerateImage",
                "ContentSafetyRejected",
                "AzureFoundry",
                _options.ImageModel,
                "ImageGeneration",
                _upstreamEndpoint,
                exception.UpstreamStatus,
                exception.ProviderRequestId,
                exception.SafeErrorCode,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (FoundryGenerationException exception)
        {
            LogFailure(
                exception,
                stopwatch,
                exception.UpstreamStatus,
                exception.ProviderRequestId,
                exception.SafeErrorCode ?? exception.GetType().Name);
            throw;
        }
        catch (Exception exception)
        {
            LogFailure(exception, stopwatch, null, null, exception.GetType().Name);
            throw;
        }
    }

    private async Task<byte[]> GenerateFromPromptAsync(
        string prompt,
        int width,
        int height,
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
            width,
            height
        });
        request.Content = new ByteArrayContent(requestBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentLength = requestBody.Length;
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var requestId = GetProviderRequestId(response);
            var errorCode = GetSafeErrorCode(responseBody);
            if ((int)response.StatusCode == 400 &&
                responseBody.Contains("content_safety_violation", StringComparison.OrdinalIgnoreCase))
            {
                throw new FoundryContentSafetyException(
                    "The image was not generated because its prompt was blocked by the content safety policy.",
                    (int)response.StatusCode,
                    requestId,
                    "content_safety_violation");
            }

            throw new FoundryGenerationException(
                $"Image generation failed with HTTP status {(int)response.StatusCode}.",
                (int)response.StatusCode,
                requestId,
                errorCode);
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

    private void LogCompleted(Stopwatch stopwatch) =>
        logger.LogInformation(
            "AI operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
            "GenerateImage",
            "Succeeded",
            stopwatch.ElapsedMilliseconds);

    private void LogFailure(
        Exception exception,
        Stopwatch stopwatch,
        int? upstreamStatus,
        string? providerRequestId,
        string? safeErrorCode) =>
        logger.LogError(
            exception,
            "AI operation {OperationName} failed for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint} with status {UpstreamStatus}, request {ProviderRequestId}, and code {SafeErrorCode} after {ElapsedMs} ms.",
            "GenerateImage",
            "AzureFoundry",
            _options.ImageModel,
            "ImageGeneration",
            _upstreamEndpoint,
            upstreamStatus,
            providerRequestId,
            safeErrorCode,
            stopwatch.ElapsedMilliseconds);

    private static string? GetProviderRequestId(HttpResponseMessage response)
    {
        foreach (var headerName in new[] { "x-ms-request-id", "apim-request-id", "request-id" })
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return SanitizeTelemetryValue(values.FirstOrDefault());
            }
        }

        return null;
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

    private static string? GetSafeErrorCode(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String)
            {
                var value = code.GetString();
                return value is { Length: > 0 and <= 128 } &&
                       value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
                    ? value
                    : null;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

}

public class FoundryGenerationException : Exception
{
    public int? UpstreamStatus { get; }
    public string? ProviderRequestId { get; }
    public string? SafeErrorCode { get; }

    public FoundryGenerationException(string message) : base(message)
    {
    }

    public FoundryGenerationException(
        string message,
        Exception innerException,
        int? upstreamStatus = null,
        string? safeErrorCode = null)
        : base(message, innerException)
    {
        UpstreamStatus = upstreamStatus;
        SafeErrorCode = safeErrorCode;
    }

    public FoundryGenerationException(
        string message,
        int? upstreamStatus,
        string? providerRequestId,
        string? safeErrorCode)
        : base(message)
    {
        UpstreamStatus = upstreamStatus;
        ProviderRequestId = providerRequestId;
        SafeErrorCode = safeErrorCode;
    }
}

public sealed class FoundryContentSafetyException : FoundryGenerationException
{
    public FoundryContentSafetyException(
        string message,
        Exception innerException,
        int? upstreamStatus = null,
        string? safeErrorCode = null)
        : base(message, innerException, upstreamStatus, safeErrorCode)
    {
    }

    public FoundryContentSafetyException(string message)
        : base(message)
    {
    }

    public FoundryContentSafetyException(
        string message,
        int? upstreamStatus,
        string? providerRequestId,
        string? safeErrorCode)
        : base(message, upstreamStatus, providerRequestId, safeErrorCode)
    {
    }
}