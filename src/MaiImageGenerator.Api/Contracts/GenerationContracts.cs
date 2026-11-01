using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Api.Contracts;

public sealed record CreateGenerationRequest(
    string Title,
    string? Description,
    string? Context,
    bool RewritePrompt,
    BoardTemplate? Template,
    IReadOnlyList<PanelRequest> Panels,
    IReadOnlyList<CharacterRequest> Characters,
    Guid? ScaffoldId = null);

public sealed record CreateBoardRequest(
    string Title,
    string? Description,
    bool RewritePrompt,
    BoardTemplate? Template,
    IReadOnlyList<PanelRequest> Panels,
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

public sealed record CreatePanelAttemptRequest(
    Guid AttemptId, long ExpectedRevision, bool Retry, string? Prompt = null, Guid? SourceVersionId = null);

public sealed record SelectPanelVersionRequest(Guid CommandId, Guid VersionId, long ExpectedRevision);

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
    GenerationCompleteness Completeness = GenerationCompleteness.None,
    GenerationRunResponse? Run = null,
    long Revision = 0,
    bool CanEditPanels = false,
    string? PendingCommandId = null,
    GenerationRunBudget? Budget = null);

public sealed record GenerationRunResponse(
    string RunId,
    int SnapshotVersion,
    DateTimeOffset AcceptedAt,
    string PromptTemplateVersion,
    string LayoutVersion,
    GenerationModelSettings Settings,
    IReadOnlyList<GenerationAttempt> Attempts,
    IReadOnlyList<GenerationReferenceVersionResponse> References);

public sealed record GenerationReferenceVersionResponse(string ReferenceId, string ContentSha256);

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

public sealed record CharacterGenerationResponse(
    string Id,
    string Name,
    IReadOnlyList<CharacterReferenceResponse> References);

public sealed record CharacterReferenceResponse(string ReferenceId, string FileName, Uri? ContentUri = null);

public sealed record GenerationSummaryResponse(
    Guid GenerationId,
    string Title,
    DateTimeOffset CreatedAt,
    GenerationStatus Status,
    Uri? ThumbnailUri,
    int PanelCount = 0,
    int CompletedPanelCount = 0,
    int FailedPanelCount = 0,
    GenerationCompleteness Completeness = GenerationCompleteness.None);