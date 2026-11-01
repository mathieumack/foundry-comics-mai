using MaiImageGenerator.Api.Configuration;

namespace MaiImageGenerator.Shared.Data.Entities;

public sealed class GenerationInputSnapshot
{
    public int Version { get; init; } = 1;
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Context { get; init; }
    public bool RewritePrompt { get; init; }
    public BoardTemplate Template { get; init; }
    public string PromptTemplateVersion { get; init; } = string.Empty;
    public string LayoutVersion { get; init; } = string.Empty;
    public GenerationModelSettings Settings { get; init; } = new();
    public List<GenerationInputPanel> Panels { get; init; } = [];
    public List<GenerationInputCharacter> Characters { get; init; } = [];
}

public sealed class GenerationModelSettings
{
    public string Provider { get; init; } = "Microsoft Foundry";
    public string PromptDeployment { get; init; } = string.Empty;
    public string ImageDeployment { get; init; } = string.Empty;
    public string? PromptModelVersion { get; init; }
    public string? ImageModelVersion { get; init; }
    public int ImageSize { get; init; }
    public int MaxParallelImages { get; init; }
    public int ImageRetryCount { get; init; }
    public int ImageRetryBaseDelayMilliseconds { get; init; }

    public FoundryOptions ToOptions(string endpoint) => new()
    {
        Endpoint = endpoint,
        PromptModel = PromptDeployment,
        ImageModel = ImageDeployment,
        ImageSize = ImageSize,
        MaxParallelImages = MaxParallelImages,
        ImageRetryCount = ImageRetryCount,
        ImageRetryBaseDelayMilliseconds = ImageRetryBaseDelayMilliseconds
    };
}

public sealed class GenerationInputPanel
{
    public int Number { get; init; }
    public string Prompt { get; init; } = string.Empty;
    public List<string> CharacterIds { get; init; } = [];
}

public sealed class GenerationInputCharacter
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public List<GenerationInputReference> References { get; init; } = [];
}

public sealed class GenerationInputReference
{
    public string Id { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string BlobName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
}

public sealed class GenerationAttempt
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "processing";
}
