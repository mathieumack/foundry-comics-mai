using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Api.Services;

public sealed class ProjectService(MaiImageGeneratorDbContext dbContext)
{
    public async Task<ProjectResponse> CreateAsync(
        string ownerId,
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var now = DateTimeOffset.UtcNow;
        var project = new ProjectDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = ownerId,
            SchemaVersion = 2,
            Type = request.Type,
            Title = request.Title.Trim(),
            Context = request.Context?.Trim(),
            SourceIdea = request.SourceIdea?.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(project);
    }

    public Task<ProjectDocument?> FindAsync(string ownerId, Guid projectId, CancellationToken cancellationToken)
    {
        var id = projectId.ToString("D");
        return dbContext.Projects
            .WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectResponse>> ListAsync(
        string ownerId,
        CancellationToken cancellationToken)
    {
        var projects = await dbContext.Projects
            .WithPartitionKey(ownerId)
            .Where(item => item.OwnerId == ownerId)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);
        return projects.Select(ToResponse).ToArray();
    }

    public async Task<ProjectResponse> UpdateAsync(
        ProjectDocument project,
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request, project.Type, project.SourceIdea);
        project.Title = request.Title.Trim();
        project.Context = request.Context?.Trim();
        project.SourceIdea = request.SourceIdea?.Trim() ?? project.SourceIdea;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(project);
    }

    public async Task<ProjectCharacterResponse> AddCharacterAsync(
        ProjectDocument project,
        ProjectCharacterRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var character = new ProjectCharacter
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim()
        };
        project.Characters.Add(character);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(character);
    }

    public async Task<ProjectCharacterResponse?> UpdateCharacterAsync(
        ProjectDocument project,
        string characterId,
        ProjectCharacterRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var character = FindCharacter(project, characterId);
        if (character is null)
        {
            return null;
        }

        character.Name = request.Name.Trim();
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(character);
    }

    public async Task<bool> DeleteCharacterAsync(
        ProjectDocument project,
        string characterId,
        CancellationToken cancellationToken)
    {
        var character = FindCharacter(project, characterId);
        if (character is null)
        {
            return false;
        }

        project.Characters.Remove(character);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ProjectCharacterResponse> AddReferenceAsync(
        ProjectDocument project,
        ProjectCharacter character,
        ReferenceDocument reference,
        CancellationToken cancellationToken)
    {
        character.References.Add(new ProjectCharacterReference
        {
            Id = reference.Id,
            FileName = reference.FileName
        });
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(character);
    }

    public static ProjectCharacter? FindCharacter(ProjectDocument project, string characterId) =>
        project.Characters.SingleOrDefault(character =>
            string.Equals(character.Id, characterId, StringComparison.Ordinal));

    public static bool HasReference(ProjectDocument project, string characterId, string referenceId) =>
        FindCharacter(project, characterId)?.References.Any(reference =>
            string.Equals(reference.Id, referenceId, StringComparison.Ordinal)) == true;

    public static ProjectResponse ToResponse(ProjectDocument project) => new(
        Guid.Parse(project.Id),
        project.Title,
        project.Context,
        project.Type,
        project.SourceIdea,
        project.CreatedAt,
        project.UpdatedAt,
        project.Characters.Select(ToResponse).ToArray());

    public static ProjectCharacterResponse ToResponse(ProjectCharacter character) => new(
        character.Id,
        character.Name,
        character.References.Select(reference =>
            new CharacterReferenceResponse(reference.Id, reference.FileName)).ToArray());

    private static void Validate(CreateProjectRequest request) =>
        Validate(request, request.Type, null);

    private static void Validate(
        CreateProjectRequest request,
        ProjectType projectType,
        string? existingSourceIdea)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("A project title is required.");
        }

        if (projectType == ProjectType.Book &&
            string.IsNullOrWhiteSpace(request.SourceIdea) &&
            string.IsNullOrWhiteSpace(existingSourceIdea))
        {
            throw new ArgumentException("A source idea is required for a book.");
        }
    }

    private static void Validate(ProjectCharacterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("A character name is required.");
        }
    }
}
