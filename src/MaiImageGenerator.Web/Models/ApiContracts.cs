namespace MaiImageGenerator.Web.Models;

public sealed record CreateProjectRequest(
    string Title,
    string? Context,
    ProjectType Type = ProjectType.Comic,
    string? SourceIdea = null,
    Guid? ScaffoldId = null);

public sealed record ProjectResponse(
    Guid ProjectId,
    string Title,
    string? Context,
    ProjectType Type,
    string? SourceIdea,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProjectCharacterResponse> Characters,
    IReadOnlyList<AiGenerationTrace>? ScaffoldTraces = null);

public enum ProjectType
{
    Comic,
    Book
}

public sealed record GenerateBookPlanRequest(int ChapterCount);

public sealed record UpdateBookPlanRequest(IReadOnlyList<BookChapterRequest> Chapters);

public sealed record BookChapterRequest(
    string Id,
    int Number,
    string Title,
    string Summary,
    string CharacterEvents,
    string Continuity,
    string? Prose,
    string? ImagePrompt);

public sealed record BookPlanResponse(
    GenerationStatus Status,
    bool IsGenerating,
    string? Error,
    IReadOnlyList<BookChapterResponse> Chapters,
    IReadOnlyList<AiGenerationTrace>? Traces = null);

public sealed record BookChapterResponse(
    string Id,
    int Number,
    string Title,
    string Summary,
    string CharacterEvents,
    string Continuity,
    string? ProsePrompt,
    string? Prose,
    GenerationStatus ProseStatus,
    string? ProseError,
    string? ImagePrompt,
    Uri? ImageUri,
    GenerationStatus ImageStatus,
    string? ImageError,
    IReadOnlyList<AiGenerationTrace>? ProseTraces = null,
    IReadOnlyList<AiGenerationTrace>? ImageTraces = null);

public sealed record ProjectCharacterResponse(
    string CharacterId,
    string Name,
    IReadOnlyList<CharacterReferenceResponse> References);

public sealed record ProjectCharacterRequest(string Name);

public sealed record CreateBoardRequest(
    string Title,
    string? Description,
    bool RewritePrompt,
    BoardTemplate Template,
    IReadOnlyList<PanelRequest> Panels,
    Guid? ScaffoldId = null);

public sealed record CreateGenerationRequest(
    string Title,
    string? Description,
    string? Context,
    bool RewritePrompt,
    BoardTemplate Template,
    IReadOnlyList<PanelRequest> Panels,
    IReadOnlyList<CharacterRequest> Characters,
    Guid? ScaffoldId = null);

public sealed record CreateProjectScaffoldRequest(string StoryDescription, int PanelCount);

public sealed record ProjectScaffoldResponse(
    string Title,
    string Description,
    string Context,
    IReadOnlyList<string> PanelPrompts,
    IReadOnlyList<AiGenerationTrace>? Traces = null,
    Guid? ScaffoldId = null);

public sealed record PanelRequest(string Prompt, IReadOnlyList<string> CharacterIds);

public sealed record CharacterRequest(
    string Id,
    string Name,
    IReadOnlyList<string> ReferenceIds);

public sealed record ReferenceUploadResponse(string ReferenceId, string FileName);

public sealed record GenerationResponse(
    Guid GenerationId,
    string Title,
    string? Description,
    string? Context,
    DateTimeOffset CreatedAt,
    bool RewritePrompt,
    BoardTemplate Template,
    IReadOnlyList<CharacterGenerationResponse> Characters,
    GenerationStatus Status,
    int PanelCount,
    int CompletedPanelCount,
    IReadOnlyList<PanelGenerationResponse> Panels,
    Uri? BoardUri,
    string? Error,
    IReadOnlyList<AiGenerationTrace>? ScaffoldTraces = null,
    int FailedPanelCount = 0,
    GenerationCompleteness? Completeness = null,
    long Revision = 0,
    bool CanEditPanels = false,
    string? PendingCommandId = null,
    GenerationRunBudget? Budget = null);

public sealed record GenerationRunBudget(
    int Version,
    int MaxModelCalls,
    int MaxAttempts,
    int MaxRetriesPerStage,
    DateTimeOffset Deadline,
    int ReservedModelCalls,
    int ReservedAttempts,
    string? StopReason,
    IReadOnlyList<GenerationStage> Stages,
    bool HistoricalConsumptionKnown = true);

public sealed record GenerationStage(
    string Id, string Operation, string? AttemptId, int ReservedCalls,
    string Status, DateTimeOffset UpdatedAt, string? StopReason);

public sealed record PanelGenerationResponse(
    int Number,
    string Prompt,
    IReadOnlyList<string> CharacterIds,
    PanelGenerationStatus Status,
    string? EffectivePrompt,
    Uri? ImageUri,
    string? Error,
    IReadOnlyList<AiGenerationTrace>? Traces = null,
    PanelGenerationIssue? Issue = null,
    string? SelectedVersionId = null,
    IReadOnlyList<PanelVersionResponse>? Versions = null);

public sealed record PanelVersionResponse(
    string Id,
    string Prompt,
    string? EffectivePrompt,
    PanelGenerationStatus Status,
    Uri? ImageUri,
    PanelGenerationIssue? Issue,
    IReadOnlyList<AiGenerationTrace> Traces,
    DateTimeOffset CreatedAt);

public sealed record CreatePanelAttemptRequest(
    Guid AttemptId, long ExpectedRevision, bool Retry, string? Prompt = null, Guid? SourceVersionId = null);

public sealed record SelectPanelVersionRequest(Guid CommandId, Guid VersionId, long ExpectedRevision);

public sealed record CharacterGenerationResponse(
    string Id,
    string Name,
    IReadOnlyList<CharacterReferenceResponse> References);

public sealed record CharacterReferenceResponse(string ReferenceId, string FileName);

public sealed record GenerationSummaryResponse(
    Guid GenerationId,
    string Title,
    DateTimeOffset CreatedAt,
    GenerationStatus Status,
    Uri? ThumbnailUri,
    int PanelCount = 0,
    int CompletedPanelCount = 0,
    int FailedPanelCount = 0,
    GenerationCompleteness? Completeness = null);

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