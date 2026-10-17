namespace MaiImageGenerator.Web.Models;

public sealed record CreateProjectRequest(
    string Title,
    string? Context,
    ProjectType Type = ProjectType.Comic,
    string? SourceIdea = null);

public sealed record ProjectResponse(
    Guid ProjectId,
    string Title,
    string? Context,
    ProjectType Type,
    string? SourceIdea,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProjectCharacterResponse> Characters);

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
    IReadOnlyList<BookChapterResponse> Chapters);

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
    string? ImageError);

public sealed record ProjectCharacterResponse(
    string CharacterId,
    string Name,
    IReadOnlyList<CharacterReferenceResponse> References);

public sealed record CreateBoardRequest(
    string Title,
    string? Description,
    bool RewritePrompt,
    BoardTemplate Template,
    IReadOnlyList<PanelRequest> Panels);

public sealed record CreateGenerationRequest(
    string Title,
    string? Description,
    string? Context,
    bool RewritePrompt,
    BoardTemplate Template,
    IReadOnlyList<PanelRequest> Panels,
    IReadOnlyList<CharacterRequest> Characters);

public sealed record CreateProjectScaffoldRequest(string StoryDescription, int PanelCount);

public sealed record ProjectScaffoldResponse(
    string Title,
    string Description,
    string Context,
    IReadOnlyList<string> PanelPrompts);

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
    string? Error);

public sealed record PanelGenerationResponse(
    int Number,
    string Prompt,
    IReadOnlyList<string> CharacterIds,
    PanelGenerationStatus Status,
    string? EffectivePrompt,
    Uri? ImageUri,
    string? Error);

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
    Uri? ThumbnailUri);

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