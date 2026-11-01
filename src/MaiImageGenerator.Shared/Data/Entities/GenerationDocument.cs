namespace MaiImageGenerator.Shared.Data.Entities;

public sealed class GenerationDocument
{
    public string Id { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 3;
    public GenerationInputSnapshot? InputSnapshot { get; set; }
    public List<GenerationAttempt> Attempts { get; set; } = [];
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
    public long Revision { get; set; }
    public long ComposedRevision { get; set; }
    public long SelectedRevision { get; set; }
    public string? PendingCommandId { get; set; }
    public string? PendingCommandKind { get; set; }
    public int? PendingPanelNumber { get; set; }
    public string? CommandExecutionId { get; set; }
    public DateTimeOffset? CommandLeaseExpiresAt { get; set; }
    public List<PanelSelectionCommand> SelectionCommands { get; set; } = [];
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
    public PanelGenerationIssue? Issue { get; set; }
    public List<AiGenerationTrace> Traces { get; set; } = [];
    public string? SelectedVersionId { get; set; }
    public List<PanelVersion> Versions { get; set; } = [];
}

public sealed class PanelVersion
{
    public string Id { get; set; } = string.Empty;
    public string? SourceVersionId { get; set; }
    public bool IsRetry { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? EffectivePrompt { get; set; }
    public PanelGenerationStatus Status { get; set; }
    public string? ImageBlobName { get; set; }
    public PanelGenerationIssue? Issue { get; set; }
    public List<AiGenerationTrace> Traces { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PanelSelectionCommand
{
    public string Id { get; set; } = string.Empty;
    public int PanelNumber { get; set; }
    public string VersionId { get; set; } = string.Empty;
}

public enum PanelGenerationIssue
{
    ProviderThrottled,
    TechnicalFailure,
    ContentSafetyRejected
}

public enum GenerationCompleteness
{
    None,
    Partial,
    Complete
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