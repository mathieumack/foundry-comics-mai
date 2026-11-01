using Azure;
using Azure.Storage.Blobs;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Api.Services;

public interface IEpubExportSource
{
    Task<ProjectDocument?> FindProjectAsync(string ownerId, Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GenerationDocument>> ListBoardsAsync(string ownerId, string projectId, CancellationToken cancellationToken);
    Task<Stream?> OpenImageAsync(string ownerId, Guid artifactId, string blobName, CancellationToken cancellationToken);
}

public sealed class EpubExportSource(
    MaiImageGeneratorDbContext dbContext,
    BlobServiceClient blobServiceClient) : IEpubExportSource
{
    public Task<ProjectDocument?> FindProjectAsync(string ownerId, Guid projectId, CancellationToken cancellationToken) =>
        dbContext.Projects.AsNoTracking().WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(project => project.Id == projectId.ToString("D") && project.OwnerId == ownerId,
                cancellationToken);

    public async Task<IReadOnlyList<GenerationDocument>> ListBoardsAsync(
        string ownerId, string projectId, CancellationToken cancellationToken) =>
        await dbContext.Generations.AsNoTracking().WithPartitionKey(ownerId)
            .Where(board => board.OwnerId == ownerId && board.ProjectId == projectId)
            .ToListAsync(cancellationToken);

    public async Task<Stream?> OpenImageAsync(
        string ownerId, Guid artifactId, string blobName, CancellationToken cancellationToken)
    {
        if (!blobName.StartsWith(ArtifactPathBuilder.GetGenerationPrefix(ownerId, artifactId), StringComparison.Ordinal))
        {
            throw new EpubExportException("An image does not belong to this project. Regenerate it, then retry export.");
        }

        try
        {
            return await blobServiceClient.GetBlobContainerClient("generations")
                .GetBlobClient(blobName).OpenReadAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }
}
