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
    IReadOnlyList<AiGenerationTrace>? ScaffoldTraces = null);

public sealed record PanelGenerationResponse(
    int Number,
    string Prompt,
    IReadOnlyList<string> CharacterIds,
    PanelGenerationStatus Status,
    string? EffectivePrompt,
    Uri? ImageUri,
    string? Error,
    IReadOnlyList<AiGenerationTrace>? Traces = null);

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