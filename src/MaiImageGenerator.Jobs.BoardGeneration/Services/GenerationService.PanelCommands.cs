using Azure;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed partial class GenerationService
{
    private async Task ProcessPanelCommandAsync(
        BoardGenerationRequestMessage request,
        GenerationDocument document,
        ProjectDocument project,
        CancellationToken cancellationToken)
    {
        if (project.DeletionRequestedAt is not null ||
            !PanelVersionCommands.Matches(document, request.CommandId, request.Revision))
            return;
        if (document.PendingCommandKind != request.Operation || document.PendingPanelNumber != request.PanelNumber)
            throw new InvalidDataException("The panel command does not match the saved operation.");
        if (PanelVersionCommands.HasActiveLease(document, _commandTime.GetUtcNow()))
            throw new PanelCommandBusyException(document.CommandLeaseExpiresAt!.Value);

        var executionId = PanelVersionCommands.Claim(document, _commandTime.GetUtcNow());
        var attempt = new GenerationAttempt { Id = executionId, StartedAt = _commandTime.GetUtcNow() };
        document.Attempts.Add(attempt);
        using var executionTimeout = new CancellationTokenSource(PanelVersionCommands.ExecutionTimeout, _commandTime);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, executionTimeout.Token);
        var token = deadline.Token;
        // Claim and its ETag must be committed before any provider or artifact side effects.
        await CheckpointAsync(null, token);
        PanelVersion? version = null;
        try
        {
            if (request.Operation == "panel")
                version = document.Panels.Single(panel => panel.Number == request.PanelNumber)
                    .Versions.Single(item => item.Id == request.CommandId);
            var snapshot = ValidateCommandSnapshot(document);
            var settings = snapshot.Settings.ToOptions(string.Empty);
            var composer = new ImageComposer(Options.Create(settings));
            if (request.Operation == "compose")
            {
                var name = CommandArtifactName("board");
                var board = await TryReadCommandArtifactAsync(name, token);
                if (board is null)
                {
                    var images = new List<byte[]?>(document.Panels.Count);
                    foreach (var panel in document.Panels)
                    {
                        // Null selections keep the existing placeholder semantics; a saved image
                        // that has disappeared is an error, never a request to regenerate a sibling.
                        if (panel.ImageBlobName is null)
                        {
                            if (panel.Status == PanelGenerationStatus.Completed)
                                throw new InvalidDataException("A selected image is missing.");
                            images.Add(null);
                            continue;
                        }
                        ValidateArtifactOwner(panel.ImageBlobName);
                        var bytes = (await _container.GetBlobClient(panel.ImageBlobName)
                            .DownloadContentAsync(token)).Value.Content.ToArray();
                        await ValidateImageAsync(bytes, token);
                        images.Add(bytes);
                    }
                    board = await composer.BuildBoardAsync(snapshot.Title, images, snapshot.Template, token);
                    EnsureOwnership();
                    await UploadCommandArtifactAsync(name, board, token);
                }
                await CheckpointAsync(() =>
                {
                    document.BoardBlobName = name;
                    document.ComposedRevision = request.Revision!.Value;
                    Finish(GenerationStatus.Completed, "completed");
                }, token);
                await NotifyCompletedAsync(request, request.PanelNumber!.Value, CancellationToken.None);
                return;
            }

            var target = document.Panels.Single(panel => panel.Number == request.PanelNumber);
            version = target.Versions.Single(item => item.Id == request.CommandId);
            var artifactName = CommandArtifactName($"panel-{target.Number}");
            var recovered = await TryReadCommandArtifactAsync(artifactName, token);
            if (recovered is null)
            {
                var client = foundryClient.ForSnapshot(snapshot);
                var coordinator = new ImageGenerationCoordinator(settings);
                await CheckpointAsync(() =>
                {
                    version.Status = PanelGenerationStatus.Processing;
                    version.Issue = null;
                }, token);
                if (version.EffectivePrompt is null)
                {
                    if (!version.IsRetry)
                    {
                        // A revised prompt is literal, not a request for another rewrite.
                        version.EffectivePrompt = version.Prompt;
                    }
                    else
                    {
                        var source = target.Versions.Single(item => item.Id == version.SourceVersionId);
                        if (source.EffectivePrompt is not null)
                            version.EffectivePrompt = source.EffectivePrompt;
                        else if (snapshot.RewritePrompt)
                        {
                            var trace = new AiGenerationTrace { Operation = "prompt-rewrite", AttemptId = executionId };
                            await CheckpointAsync(() => version.Traces.Add(trace), token);
                            version.EffectivePrompt = await client.RewritePromptAsync(
                                snapshot.Context, version.Prompt, token, trace);
                        }
                        else
                            version.EffectivePrompt = version.Prompt;
                    }
                    await CheckpointAsync(null, token);
                }
                var references = await LoadSnapshotReferencesAsync(document, snapshot, token);
                var (characters, sheet) = await PrepareReferencesAsync(
                    request.OwnerId, snapshot, references, composer, project, target, token);
                var prompt = characters.Length == 0 ? version.EffectivePrompt :
                    $"Use the reference sheet only as identity guidance for {string.Join(", ", characters)}. Preserve their identity and create this new scene: {version.EffectivePrompt}";
                var layout = BoardTemplateDefinitions.Resolve(snapshot.Template, document.Panels.Count);
                var slot = layout.Slots[document.Panels.IndexOf(target)];
                await NotifyStartedAsync(request, target.Number, token);
                var bytes = await coordinator.ExecuteWithRetryAsync(
                    "comic-panel",
                    async retryToken =>
                    {
                        var trace = new AiGenerationTrace { Operation = "panel-image", AttemptId = executionId };
                        await CheckpointAsync(() =>
                        {
                            version.Issue = null;
                            version.Traces.Add(trace);
                        }, retryToken);
                        return await client.GenerateAsync(prompt!, sheet, slot.ColumnSpan > slot.RowSpan, retryToken, trace);
                    },
                    IsTransient,
                    IsThrottled,
                    token,
                    async (exception, retryToken) =>
                    {
                        if (IsThrottled(exception))
                            await CheckpointAsync(() => version.Issue = PanelGenerationIssue.ProviderThrottled, retryToken);
                    });
                await ValidateImageAsync(bytes, token);
                EnsureOwnership();
                await UploadCommandArtifactAsync(artifactName, bytes, token);
            }
            await CheckpointAsync(() =>
            {
                version.Status = PanelGenerationStatus.Completed;
                version.ImageBlobName = artifactName;
                version.Issue = null;
                // Candidate completion never touches the selected panel projection or old board.
                Finish(GenerationStatus.Completed, "completed");
            }, token);
            await NotifyCompletedAsync(request, target.Number, CancellationToken.None);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or
                                        PanelCommandCheckpointException or PanelCommandLostException)
        {
            // In particular, do not attempt a failure checkpoint with a stale ETag/claim.
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await CheckpointAsync(() =>
            {
                if (version is not null)
                {
                    version.Status = PanelGenerationStatus.Pending;
                    version.Issue = null;
                }
                attempt.Status = "canceled";
                attempt.CompletedAt = _commandTime.GetUtcNow();
                document.Status = GenerationStatus.Pending;
                document.CommandExecutionId = null;
                document.CommandLeaseExpiresAt = null;
            }, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await CheckpointAsync(() =>
            {
                if (version is not null)
                {
                    version.Status = PanelGenerationStatus.Failed;
                    version.Issue = GetPanelIssue(exception);
                    version.ImageBlobName = null;
                }
                document.Error = request.Operation == "compose" ? "The selected panel versions could not be composed." : null;
                Finish(request.Operation == "panel" ? GenerationStatus.Completed : GenerationStatus.Failed, "failed");
            }, CancellationToken.None);
            await NotifyCompletedAsync(request, request.PanelNumber!.Value, CancellationToken.None);
        }

        string CommandArtifactName(string prefix) => ArtifactPathBuilder.GetGenerationBlobName(
            request.OwnerId, Guid.Parse(document.Id), $"{prefix}-{Guid.Parse(request.CommandId!):N}.png");

        void ValidateArtifactOwner(string name)
        {
            var prefix = ArtifactPathBuilder.GetGenerationBlobName(request.OwnerId, Guid.Parse(document.Id), string.Empty);
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("A selected image has an invalid artifact path.");
        }

        void EnsureOwnership()
        {
            if (!PanelVersionCommands.Matches(document, request.CommandId, request.Revision) ||
                document.PendingCommandKind != request.Operation || document.PendingPanelNumber != request.PanelNumber ||
                !PanelVersionCommands.OwnsExecution(document, executionId, _commandTime.GetUtcNow()))
                throw new PanelCommandLostException();
        }

        async Task CheckpointAsync(Action? update, CancellationToken checkpointToken)
        {
            checkpointToken.ThrowIfCancellationRequested();
            EnsureOwnership();
            update?.Invoke();
            document.UpdatedAt = _commandTime.GetUtcNow();
            try
            {
                await dbContext.SaveChangesAsync(checkpointToken);
            }
            catch (Exception exception) when (exception is not DbUpdateConcurrencyException)
            {
                // A failed save may have committed. Leave recovery to the persisted lease and
                // immutable artifact instead of turning an uncertain checkpoint into terminal failure.
                // This includes cancellation during SaveChanges, as opposed to cancellation
                // observed before starting the write.
                throw new PanelCommandCheckpointException(exception);
            }
        }

        void Finish(GenerationStatus status, string outcome)
        {
            document.Status = status;
            document.PendingCommandId = null;
            document.PendingCommandKind = null;
            document.PendingPanelNumber = null;
            document.CommandExecutionId = null;
            document.CommandLeaseExpiresAt = null;
            attempt.Status = outcome;
            attempt.CompletedAt = _commandTime.GetUtcNow();
        }
    }

    private static GenerationInputSnapshot ValidateCommandSnapshot(GenerationDocument document)
    {
        var snapshot = document.InputSnapshot;
        if (document.SchemaVersion < 5 || snapshot is null || snapshot.Version != 1 || snapshot.RunId != document.Id ||
            snapshot.PromptTemplateVersion != BoardInputVersions.PromptTemplate ||
            snapshot.LayoutVersion != BoardInputVersions.Layout ||
            snapshot.Settings.Provider != "Microsoft Foundry" ||
            string.IsNullOrWhiteSpace(snapshot.Settings.PromptDeployment) ||
            string.IsNullOrWhiteSpace(snapshot.Settings.ImageDeployment) ||
            !snapshot.Panels.Select(panel => panel.Number).SequenceEqual(document.Panels.Select(panel => panel.Number)))
            throw new InvalidDataException("The generation input snapshot is unsupported or invalid.");
        return snapshot;
    }

    private async Task<byte[]?> TryReadCommandArtifactAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = (await _container.GetBlobClient(name).DownloadContentAsync(cancellationToken)).Value.Content.ToArray();
            await ValidateImageAsync(bytes, cancellationToken);
            return bytes;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    private async Task UploadCommandArtifactAsync(string name, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            await _container.GetBlobClient(name).UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "image/png" }
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // An earlier execution won. Never overwrite it; verify that its artifact is usable.
            _ = await TryReadCommandArtifactAsync(name, cancellationToken)
                ?? throw new InvalidDataException("The command artifact could not be recovered.");
        }
    }

    private static async Task ValidateImageAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var image = await Image.LoadAsync(stream, cancellationToken);
    }
}

public sealed class PanelCommandBusyException(DateTimeOffset retryAt)
    : Exception("The panel command is already executing.")
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}

public sealed class PanelCommandLostException() : Exception("The panel command execution is no longer current.");

public sealed class PanelCommandCheckpointException(Exception innerException)
    : Exception("The panel command checkpoint could not be confirmed.", innerException);
