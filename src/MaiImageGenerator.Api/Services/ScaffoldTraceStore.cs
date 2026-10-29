using Azure;
using Azure.Storage.Blobs;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Api.Services;

public sealed class ScaffoldTraceStore(
    BlobServiceClient blobServiceClient,
    ILogger<ScaffoldTraceStore> logger)
{
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("generations");

    public async Task<ProjectScaffoldResponse> SaveAsync(
        string ownerId,
        ProjectScaffoldResponse scaffold,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        try
        {
            var blob = _container.GetBlobClient(GetBlobName(ownerId, id));
            await blob.UploadAsync(
                BinaryData.FromObjectAsJson(scaffold.Traces ?? []),
                cancellationToken: cancellationToken);
            return scaffold with { ScaffoldId = id };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Scaffold trace persistence failed; failure type {FailureType}.", exception.GetType().Name);
            return scaffold;
        }
    }

    public async Task<List<AiGenerationTrace>> ReadAsync(
        string ownerId,
        Guid? scaffoldId,
        CancellationToken cancellationToken)
    {
        if (scaffoldId is null)
        {
            return [];
        }

        try
        {
            var result = await _container.GetBlobClient(GetBlobName(ownerId, scaffoldId.Value))
                .DownloadContentAsync(cancellationToken);
            return result.Value.Content.ToObjectFromJson<List<AiGenerationTrace>>() ?? [];
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            throw new ArgumentException("The scaffold was not found for the authenticated owner.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Scaffold trace retrieval failed; failure type {FailureType}.", exception.GetType().Name);
            return [];
        }
    }

    private static string GetBlobName(string ownerId, Guid id) =>
        ArtifactPathBuilder.GetGenerationBlobName(ownerId, id, "scaffold-trace.json");
}
