namespace MaiImageGenerator.Shared.Data.Entities;

public sealed class GenerationDocument
{
    public string Id { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 3;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Context { get; set; }
    public bool RewritePrompt { get; set; } = true;
    public Guid? ScaffoldId { get; set; }
    public List<AiGenerationTrace> ScaffoldTraces { get; set; } = [];
    public BoardTemplate Template { get; set; }
    public GenerationStatus Status { get; set; }
    public List<GenerationPanel> Panels { get; set; } = [];
    public string? BoardBlobName { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ETag { get; set; }
}

public sealed class GenerationPanel
{
    public int Number { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? EffectivePrompt { get; set; }
    public List<string> CharacterIds { get; set; } = [];
    public PanelGenerationStatus Status { get; set; }
    public string? ImageBlobName { get; set; }
    public string? Error { get; set; }
    public List<AiGenerationTrace> Traces { get; set; } = [];
}

public enum GenerationStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Canceled
}

public enum PanelGenerationStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}