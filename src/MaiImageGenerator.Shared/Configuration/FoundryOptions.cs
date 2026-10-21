namespace MaiImageGenerator.Api.Configuration;

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    public string Endpoint { get; init; } = string.Empty;
    public string PromptModel { get; init; } = "MAI-Thinking-1";
    public string ImageModel { get; init; } = "MAI-Image-2.5-Pro";
    public int ImageSize { get; init; } = 1024;
    public int MaxParallelImages { get; init; } = 3;
    public int ImageRetryCount { get; init; } = 3;
    public int ImageRetryBaseDelayMilliseconds { get; init; } = 1000;
}