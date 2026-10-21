using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Api.Services;

public sealed class ProjectDeletionService(
    IProjectDeletionRepository repository,
    IProjectArtifactStore artifactStore,
    ILogger<ProjectDeletionService> logger)
{
    public async Task<bool> DeleteAsync(
        string ownerId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var deletion = await repository.LoadAsync(ownerId, projectId, cancellationToken);
            if (deletion is null)
            {
                return false;
            }

            logger.LogInformation(
                "Project deletion started for {ProjectId} with {GenerationCount} generations and {ReferenceCount} exclusive references",
                projectId,
                deletion.Generations.Count,
                deletion.ReferenceIds.Count);
            await artifactStore.DeleteAsync(
                ownerId,
                projectId,
                deletion.Generations.Select(item => Guid.Parse(item.Id)).ToArray(),
                deletion.ReferenceIds,
                cancellationToken);
            await repository.DeleteGenerationsAsync(deletion.Generations, cancellationToken);
            await repository.DeleteReferencesAsync(deletion.References, cancellationToken);
            await repository.DeleteProjectAsync(deletion.Project, cancellationToken);
        }
        catch (ProjectDeletionConflictException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Project deletion failed for {ProjectId}; cleanup can be retried", projectId);
            throw new ProjectDeletionException("Project cleanup did not complete.", exception);
        }

        logger.LogInformation("Project deletion completed for {ProjectId}", projectId);
        return true;
    }
}

public sealed class ProjectDeletionException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed class ProjectDeletionConflictException()
    : Exception("Project generation is still in progress.");

public sealed record ProjectDeletionData(
    ProjectDocument Project,
    IReadOnlyList<GenerationDocument> Generations,
    IReadOnlyList<ReferenceDocument> References,
    IReadOnlyList<string> ReferenceIds);

public interface IProjectDeletionRepository
{
    Task<ProjectDeletionData?> LoadAsync(string ownerId, Guid projectId, CancellationToken cancellationToken);
    Task DeleteGenerationsAsync(
        IReadOnlyList<GenerationDocument> generations,
        CancellationToken cancellationToken);
    Task DeleteReferencesAsync(
        IReadOnlyList<ReferenceDocument> references,
        CancellationToken cancellationToken);
    Task DeleteProjectAsync(ProjectDocument project, CancellationToken cancellationToken);
}

public sealed class CosmosProjectDeletionRepository(MaiImageGeneratorDbContext dbContext)
    : IProjectDeletionRepository
{
    public async Task<ProjectDeletionData?> LoadAsync(
        string ownerId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var id = projectId.ToString("D");
        var project = await dbContext.Projects
            .WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var generations = await dbContext.Generations
            .WithPartitionKey(ownerId)
            .Where(item => item.OwnerId == ownerId && item.ProjectId == id)
            .ToListAsync(cancellationToken);
        if (generations.Any(item => item.Status == GenerationStatus.Processing) ||
            project.BookPlan?.Status == GenerationStatus.Processing ||
            project.BookPlan?.Chapters.Any(chapter =>
                chapter.ProseStatus == GenerationStatus.Processing ||
                chapter.ImageStatus == GenerationStatus.Processing) == true)
        {
            throw new ProjectDeletionConflictException();
        }

        if (project.DeletionRequestedAt is null)
        {
            project.DeletionRequestedAt = DateTimeOffset.UtcNow;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var stateChanged = false;
        foreach (var generation in generations.Where(item =>
                     item.Status is GenerationStatus.Pending or GenerationStatus.Failed))
        {
            generation.Status = GenerationStatus.Canceled;
            generation.UpdatedAt = DateTimeOffset.UtcNow;
            stateChanged = true;
        }

        if (project.BookPlan?.Status is GenerationStatus.Pending or GenerationStatus.Failed)
        {
            project.BookPlan.Status = GenerationStatus.Canceled;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            stateChanged = true;
        }

        if (stateChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var linkedReferenceIds = project.Characters
            .SelectMany(character => character.References)
            .Select(reference => reference.Id)
            .ToHashSet(StringComparer.Ordinal);
        var otherProjects = await dbContext.Projects
            .WithPartitionKey(ownerId)
            .Where(item =>
                item.OwnerId == ownerId &&
                item.Id != id &&
                item.DeletionRequestedAt == null)
            .ToListAsync(cancellationToken);
        linkedReferenceIds.ExceptWith(otherProjects
            .SelectMany(item => item.Characters)
            .SelectMany(character => character.References)
            .Select(reference => reference.Id));

        var references = new List<ReferenceDocument>(linkedReferenceIds.Count);
        foreach (var referenceId in linkedReferenceIds)
        {
            var reference = await dbContext.References
                .WithPartitionKey(ownerId)
                .SingleOrDefaultAsync(
                    item => item.Id == referenceId && item.OwnerId == ownerId,
                    cancellationToken);
            if (reference is not null)
            {
                references.Add(reference);
            }
        }

        return new(project, generations, references, linkedReferenceIds.ToArray());
    }

    public async Task DeleteGenerationsAsync(
        IReadOnlyList<GenerationDocument> generations,
        CancellationToken cancellationToken)
    {
        if (generations.Count == 0)
        {
            return;
        }

        dbContext.Generations.RemoveRange(generations);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteReferencesAsync(
        IReadOnlyList<ReferenceDocument> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return;
        }

        dbContext.References.RemoveRange(references);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task DeleteProjectAsync(ProjectDocument project, CancellationToken cancellationToken)
    {
        dbContext.Projects.Remove(project);
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}

public interface IProjectArtifactStore
{
    Task DeleteAsync(
        string ownerId,
        Guid projectId,
        IReadOnlyList<Guid> generationIds,
        IReadOnlyList<string> referenceIds,
        CancellationToken cancellationToken);
}

public sealed class BlobProjectArtifactStore(BlobServiceClient blobServiceClient) : IProjectArtifactStore
{
    private readonly BlobContainerClient _generations = blobServiceClient.GetBlobContainerClient("generations");
    private readonly BlobContainerClient _references = blobServiceClient.GetBlobContainerClient("references");

    public async Task DeleteAsync(
        string ownerId,
        Guid projectId,
        IReadOnlyList<Guid> generationIds,
        IReadOnlyList<string> referenceIds,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        var generationPrefixes = generationIds
            .Append(projectId)
            .Distinct()
            .Select(id => ArtifactPathBuilder.GetGenerationPrefix(ownerId, id));
        await DeletePrefixesAsync(_generations, generationPrefixes, failures, cancellationToken);
        await DeletePrefixesAsync(
            _references,
            referenceIds.Select(id => ArtifactPathBuilder.GetReferencePrefix(ownerId, id)),
            failures,
            cancellationToken);

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more project artifacts could not be deleted.", failures);
        }
    }

    private static async Task DeletePrefixesAsync(
        BlobContainerClient container,
        IEnumerable<string> prefixes,
        ICollection<Exception> failures,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in prefixes)
        {
            try
            {
                await DeletePrefixAsync(container, prefix, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private static async Task DeletePrefixAsync(
        BlobContainerClient container,
        string prefix,
        CancellationToken cancellationToken)
    {
        await foreach (var item in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.Version | BlobStates.Snapshots,
                           prefix,
                           cancellationToken))
        {
            var blob = container.GetBlobClient(item.Name);
            if (item.VersionId is not null)
            {
                await blob.WithVersion(item.VersionId).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
            else if (item.Snapshot is not null)
            {
                await blob.WithSnapshot(item.Snapshot).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
        }

        await foreach (var item in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           prefix,
                           cancellationToken))
        {
            await container.GetBlobClient(item.Name).DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: cancellationToken);
        }
    }
}
