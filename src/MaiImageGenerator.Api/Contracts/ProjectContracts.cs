using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Api.Contracts;

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

public sealed record GenerateBookPlanRequest(int ChapterCount = 8);

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

public sealed record ProjectCharacterRequest(string Name);

public sealed record ProjectCharacterResponse(
    string CharacterId,
    string Name,
    IReadOnlyList<CharacterReferenceResponse> References);
