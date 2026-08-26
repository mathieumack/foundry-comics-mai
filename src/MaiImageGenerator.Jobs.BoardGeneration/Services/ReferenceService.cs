using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class ReferenceService(
    MaiImageGeneratorDbContext dbContext,
    BlobServiceClient blobServiceClient)
{
    public const long MaximumFileLength = 10 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> SupportedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg"
        };

    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("references");

    public async Task<ReferenceDocument> UploadAsync(
        string ownerId,
        string fileName,
        Stream file,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaximumFileLength)
        {
            throw new ArgumentException($"Reference images must be between 1 byte and {MaximumFileLength} bytes.");
        }

        if (!SupportedContentTypes.TryGetValue(contentType, out var extension))
        {
            throw new ArgumentException("Only PNG and JPEG reference images are supported.");
        }

        var id = Guid.NewGuid().ToString("N");
        var blobName = ArtifactPathBuilder.GetReferenceBlobName(ownerId, id, extension);
        var blob = _container.GetBlobClient(blobName);

        await blob.UploadAsync(file, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var document = new ReferenceDocument
        {
            Id = id,
            OwnerId = ownerId,
            FileName = fileName,
            ContentType = contentType,
            Length = file.Length,
            BlobName = blobName,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            dbContext.References.Add(document);
            await dbContext.SaveChangesAsync(cancellationToken);
            return document;
        }
        catch
        {
            await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            throw;
        }
    }

    public Task<ReferenceDocument?> GetAsync(string ownerId, string referenceId, CancellationToken cancellationToken) =>
        dbContext.References
            .WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == referenceId && item.OwnerId == ownerId, cancellationToken);

    public async Task<Stream?> OpenReadAsync(
        string ownerId,
        string referenceId,
        CancellationToken cancellationToken)
    {
        var document = await GetAsync(ownerId, referenceId, cancellationToken);
        if (document is null)
        {
            return null;
        }

        return await _container.GetBlobClient(document.BlobName).OpenReadAsync(cancellationToken: cancellationToken);
    }
}