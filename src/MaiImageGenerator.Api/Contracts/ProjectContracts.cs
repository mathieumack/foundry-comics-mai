namespace MaiImageGenerator.Api.Contracts;

public sealed record CreateProjectRequest(string Title, string? Context);

public sealed record ProjectResponse(
    Guid ProjectId,
    string Title,
    string? Context,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProjectCharacterResponse> Characters);

public sealed record ProjectCharacterRequest(string Name);

public sealed record ProjectCharacterResponse(
    string CharacterId,
    string Name,
    IReadOnlyList<CharacterReferenceResponse> References);
