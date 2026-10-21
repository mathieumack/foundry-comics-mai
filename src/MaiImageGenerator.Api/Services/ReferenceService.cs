using System.Diagnostics;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Api.Services;

public sealed class ReferenceService(
    MaiImageGeneratorDbContext dbContext,
    BlobServiceClient blobServiceClient,
    ILogger<ReferenceService> logger)
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
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaximumFileLength)
        {
            throw new ArgumentException($"Reference images must be between 1 byte and {MaximumFileLength} bytes.");
        }

        if (!SupportedContentTypes.TryGetValue(file.ContentType, out var extension))
        {
            throw new ArgumentException("Only PNG and JPEG reference images are supported.");
        }

        var id = Guid.NewGuid().ToString("N");
        var blobName = ArtifactPathBuilder.GetReferenceBlobName(ownerId, id, extension);
        var blob = _container.GetBlobClient(blobName);
        await using var content = file.OpenReadStream();
        using var scope = logger.BeginScope("OperationId:{OperationId} ReferenceId:{ReferenceId}", id, id);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Async operation {OperationName} started at stage {GenerationStage}.",
            "UploadReference",
            "BlobUpload");

        try
        {
            await blob.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = file.ContentType }
            }, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var document = new ReferenceDocument
            {
                Id = id,
                OwnerId = ownerId,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Length = file.Length,
                BlobName = blobName,
                CreatedAt = now,
                UpdatedAt = now
            };

            dbContext.References.Add(document);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Async operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                "UploadReference",
                "Succeeded",
                stopwatch.ElapsedMilliseconds);
            return document;
        }
        catch (OperationCanceledException exception)
        {
            await blob.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            logger.LogWarning(
                exception,
                "Async operation {OperationName} ended with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                "UploadReference",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                "BlobOrDatabase",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            await blob.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            logger.LogError(
                exception,
                "Async operation {OperationName} failed at stage {GenerationStage} after {ElapsedMs} ms.",
                "UploadReference",
                "BlobOrDatabase",
                stopwatch.ElapsedMilliseconds);
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
        return document is null
            ? null
            : await _container.GetBlobClient(document.BlobName).OpenReadAsync(cancellationToken: cancellationToken);
    }
}