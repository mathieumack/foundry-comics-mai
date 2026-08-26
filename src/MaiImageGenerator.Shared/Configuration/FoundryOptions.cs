namespace MaiImageGenerator.Api.Configuration;

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    public string Endpoint { get; init; } = string.Empty;
    public string PromptModel { get; init; } = "MAI-Thinking-1";
    public string ImageModel { get; init; } = "MAI-Image-2.5-Pro";
    public int ImageSize { get; init; } = 1024;
}