using Azure.Messaging.ServiceBus;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Api.Services;

public sealed partial class GenerationService
{
    public Task<GenerationResponse?> CreatePanelAttemptAsync(string ownerId, Guid generationId, int panelNumber,
        CreatePanelAttemptRequest request, CancellationToken cancellationToken) =>
        ExecutePanelCommandAsync(ownerId, generationId, request.AttemptId,
            document => PanelVersionCommands.CreateAttempt(document, panelNumber, request.AttemptId,
                request.ExpectedRevision, request.Retry, request.Prompt, request.SourceVersionId, DateTimeOffset.UtcNow),
            cancellationToken);

    public Task<GenerationResponse?> SelectPanelVersionAsync(string ownerId, Guid generationId, int panelNumber,
        SelectPanelVersionRequest request, CancellationToken cancellationToken) =>
        ExecutePanelCommandAsync(ownerId, generationId, request.CommandId,
            document => PanelVersionCommands.SelectVersion(document, panelNumber, request.CommandId,
                request.VersionId, request.ExpectedRevision, DateTimeOffset.UtcNow), cancellationToken);

    public Task<GenerationResponse?> ResumePanelCommandAsync(string ownerId, Guid generationId, Guid commandId,
        CancellationToken cancellationToken) =>
        ExecutePanelCommandAsync(ownerId, generationId, commandId, document =>
        {
            var id = commandId.ToString("D");
            if (document.PendingCommandId != id &&
                !document.Panels.SelectMany(panel => panel.Versions).Any(version => version.Id == id) &&
                !document.SelectionCommands.Any(command => command.Id == id))
                throw new ArgumentException("The accepted panel command was not found.");
            return false;
        }, cancellationToken);

    private async Task<GenerationResponse?> ExecutePanelCommandAsync(string ownerId, Guid generationId,
        Guid commandId, Func<GenerationDocument, bool> apply, CancellationToken cancellationToken)
    {
        var id = generationId.ToString("D");
        var document = await dbContext.Generations.WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (document is null) return null;
        var project = await dbContext.Projects.WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == document.ProjectId && item.OwnerId == ownerId, cancellationToken);
        if (project is null) return null;
        return await ExecutePanelCommandAsync(document, project, commandId, apply, cancellationToken);
    }

    internal async Task<GenerationResponse> ExecutePanelCommandAsync(GenerationDocument document,
        ProjectDocument project, Guid commandId, Func<GenerationDocument, bool> apply, CancellationToken cancellationToken)
    {
        if (document.OwnerId != project.OwnerId || document.ProjectId != project.Id)
            throw new ArgumentException("The board does not belong to this project.");
        if (project.DeletionRequestedAt is not null || BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow))
            throw new PanelCommandConflictException("The project is being deleted or another submission is in progress.");

        var leaseId = BoardSubmissionLease.Acquire(project, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BoardSubmissionLease.SubmissionTimeout);
        try
        {
            if (apply(document))
                await dbContext.SaveChangesAsync(timeout.Token);
            await DispatchPanelCommandAsync(document, commandId, timeout.Token);
            return ToResponse(document, project);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Discard the stale aggregate; releasing a lease must never retry its rejected mutation.
            dbContext.ChangeTracker.Clear();
            dbContext.Projects.Attach(project);
            throw;
        }
        finally
        {
            if (dbContext.Entry(project).State != EntityState.Detached)
            {
                BoardSubmissionLease.Release(project, leaseId);
                try
                {
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not release the panel submission lease; it will expire.");
                }
            }
        }
    }

    internal async Task DispatchPanelCommandAsync(GenerationDocument document, Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!PanelVersionCommands.Matches(document, commandId.ToString("D"), document.Revision)) return;
        var message = new BoardGenerationRequestMessage
        {
            SchemaVersion = 4, BoardId = document.Id, RunId = document.Id, OwnerId = document.OwnerId,
            ProjectId = document.ProjectId, Operation = document.PendingCommandKind,
            CommandId = document.PendingCommandId, PanelNumber = document.PendingPanelNumber,
            Revision = document.Revision
        };
        try
        {
            await jobsQueue.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(message))
            {
                MessageId = $"{document.Id}:{commandId:D}",
                ContentType = "application/json"
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The accepted command remains pending and can be dispatched again with the same ID.
            throw new PanelCommandDispatchException(exception);
        }
    }

    public async Task<(Stream Content, string ContentType)?> OpenPanelVersionAsync(string ownerId,
        Guid generationId, int panelNumber, Guid versionId, CancellationToken cancellationToken)
    {
        var id = generationId.ToString("D");
        var document = await dbContext.Generations.WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        var version = document?.Panels.SingleOrDefault(item => item.Number == panelNumber)?.Versions
            .SingleOrDefault(item => item.Id == versionId.ToString("D"));
        if (version?.ImageBlobName is not { } path || version.Status != PanelGenerationStatus.Completed)
            return null;
        var prefix = ArtifactPathBuilder.GetGenerationBlobName(ownerId, generationId, string.Empty);
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The panel version has an invalid artifact path.");
        return (await _container.GetBlobClient(path).OpenReadAsync(cancellationToken: cancellationToken), "image/png");
    }
}

public sealed class PanelCommandDispatchException(Exception innerException)
    : Exception("The accepted panel command could not be queued. Retry the same command.", innerException);
