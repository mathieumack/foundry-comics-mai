using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Api.Contracts;

public sealed record CreateGenerationRequest(
    string Title,
    string? Context,
    bool RewritePrompt,
    IReadOnlyList<PanelRequest> Panels,
    IReadOnlyList<CharacterRequest> Characters);

public sealed record PanelRequest(string Prompt, IReadOnlyList<string> CharacterIds);

public sealed record CharacterRequest(
    string Id,
    string Name,
    IReadOnlyList<string> ReferenceIds);

public sealed record ReferenceUploadResponse(string ReferenceId, string FileName);

public sealed record GenerationResponse(
    Guid GenerationId,
    GenerationStatus Status,
    int PanelCount,
    int CompletedPanelCount,
    IReadOnlyList<PanelGenerationResponse> Panels,
    Uri? BoardUri,
    string? Error);

public sealed record PanelGenerationResponse(
    int Number,
    PanelGenerationStatus Status,
    string? EffectivePrompt,
    Uri? ImageUri);