using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using MaiImageGenerator.Shared.Generation;
using Microsoft.Extensions.Options;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed partial class GenerationService(
    MaiImageGeneratorDbContext dbContext,
    ReferenceService referenceService,
    BlobServiceClient blobServiceClient,
    FoundryImageClient foundryClient,
    ImageComposer imageComposer,
    PanelGenerationNotifier notifier,
    ImageGenerationCoordinator imageGeneration,
    ILogger<GenerationService> logger,
    TimeProvider? commandTimeProvider = null)
{
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("generations");
    private readonly TimeProvider _commandTime = commandTimeProvider ?? TimeProvider.System;

    public async Task ProcessAsync(
        BoardGenerationRequestMessage request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var document = await dbContext.Generations
            .WithPartitionKey(request.OwnerId)
            .SingleOrDefaultAsync(
                item => item.Id == request.BoardId && item.OwnerId == request.OwnerId,
                cancellationToken)
            ?? throw new InvalidOperationException($"Generation '{request.BoardId}' was not found.");
        var project = await dbContext.Projects
            .WithPartitionKey(request.OwnerId)
            .SingleOrDefaultAsync(
                item => item.Id == request.ProjectId && item.OwnerId == request.OwnerId,
                cancellationToken)
            ?? throw new InvalidOperationException($"Project '{request.ProjectId}' was not found.");

        await ProcessAsync(request, document, project, cancellationToken);
    }

    internal async Task ProcessAsync(
        BoardGenerationRequestMessage request,
        GenerationDocument document,
        ProjectDocument project,
        CancellationToken cancellationToken)
    {
        Validate(request);
        if (document.OwnerId != request.OwnerId || project.OwnerId != request.OwnerId ||
            document.Id != request.BoardId || project.Id != request.ProjectId)
        {
            throw new InvalidDataException("The board generation message has invalid ownership.");
        }
        if (document.ProjectId != project.Id)
        {
            throw new InvalidOperationException(
                $"Generation '{request.BoardId}' does not belong to project '{request.ProjectId}'.");
        }

        if (request.SchemaVersion == 4)
        {
            await ProcessPanelCommandAsync(request, document, project, cancellationToken);
            return;
        }
        // Old deliveries must never enter the stable-path writer after panel editing begins.
        if (document.Revision > 0)
        {
            return;
        }

        if (document.Status is GenerationStatus.Completed or GenerationStatus.Canceled ||
            project.DeletionRequestedAt is not null)
        {
            return;
        }

        document.Status = GenerationStatus.Processing;
        var snapshot = document.InputSnapshot;
        var attempt = new GenerationAttempt
        {
            Id = Guid.NewGuid().ToString("D"),
            StartedAt = DateTimeOffset.UtcNow
        };
        document.Attempts.Add(attempt);
        document.Error = null;
        foreach (var failedPanel in document.Panels.Where(panel => panel.Status == PanelGenerationStatus.Failed))
        {
            failedPanel.Status = PanelGenerationStatus.Pending;
            failedPanel.Error = null;
            failedPanel.Issue = null;
        }
        document.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var client = foundryClient;
            var coordinator = imageGeneration;
            var composer = imageComposer;
            IReadOnlyDictionary<string, byte[]> retainedReferences = new Dictionary<string, byte[]>();
            if (snapshot is not null)
            {
                if (snapshot.Version != 1 || snapshot.RunId != document.Id ||
                    (request.SchemaVersion == 3 && request.RunId != snapshot.RunId) ||
                    snapshot.PromptTemplateVersion != BoardInputVersions.PromptTemplate ||
                    snapshot.LayoutVersion != BoardInputVersions.Layout ||
                    snapshot.Settings.Provider != "Microsoft Foundry" ||
                    string.IsNullOrWhiteSpace(snapshot.Settings.PromptDeployment) ||
                    string.IsNullOrWhiteSpace(snapshot.Settings.ImageDeployment) ||
                    !snapshot.Panels.Select(panel => panel.Number).SequenceEqual(document.Panels.Select(panel => panel.Number)))
                {
                    throw new InvalidDataException("The generation input snapshot is unsupported or invalid.");
                }
                client = foundryClient.ForSnapshot(snapshot);
                coordinator = new ImageGenerationCoordinator(snapshot.Settings.ToOptions(string.Empty));
                composer = new ImageComposer(Options.Create(snapshot.Settings.ToOptions(string.Empty)));
                retainedReferences = await LoadSnapshotReferencesAsync(document, snapshot, cancellationToken);
            }
            else if (document.SchemaVersion >= 4 || request.SchemaVersion == 3)
            {
                throw new InvalidDataException("The generation input snapshot is missing.");
            }

            var panelImages = new byte[]?[document.Panels.Count];
            var panelIndexesToGenerate = new List<int>();
            var template = snapshot?.Template ?? document.Template;
            var layout = BoardTemplateDefinitions.Resolve(template, document.Panels.Count);
            for (var panelIndex = 0; panelIndex < document.Panels.Count; panelIndex++)
            {
                var panel = document.Panels[panelIndex];
                if (panel.Status == PanelGenerationStatus.Completed && panel.ImageBlobName is not null)
                {
                    try
                    {
                        panelImages[panelIndex] = (await _container.GetBlobClient(panel.ImageBlobName)
                            .DownloadContentAsync(cancellationToken)).Value.Content.ToArray();
                        await NotifyCompletedAsync(request, panel.Number, cancellationToken);
                        continue;
                    }
                    catch (RequestFailedException exception) when (exception.Status == 404)
                    {
                        panel.Status = PanelGenerationStatus.Pending;
                    }
                }

                panel.Status = PanelGenerationStatus.Processing;
                panel.Error = null;
                panel.Issue = null;
                panelIndexesToGenerate.Add(panelIndex);
            }

            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            var workItems = new List<PanelWorkItem>(panelIndexesToGenerate.Count);
            foreach (var panelIndex in panelIndexesToGenerate)
            {
                var panel = document.Panels[panelIndex];
                var input = snapshot?.Panels[panelIndex];
                try
                {
                    if (snapshot is not null && panel.EffectivePrompt is not null)
                    {
                        // Reuse a prepared prompt when recovering the same accepted run.
                    }
                    else if (snapshot?.RewritePrompt ?? request.RewritePrompt)
                    {
                        var rewriteTrace = new AiGenerationTrace { Operation = "prompt-rewrite", AttemptId = attempt.Id };
                        panel.Traces.Add(rewriteTrace);
                        panel.EffectivePrompt = await client.RewritePromptAsync(
                            snapshot is null ? document.Context : snapshot.Context,
                            input?.Prompt ?? panel.Prompt,
                            cancellationToken,
                            rewriteTrace);
                    }
                    else
                    {
                        panel.EffectivePrompt = input?.Prompt ?? panel.Prompt;
                    }
                    var (selectedCharacters, referenceSheet) = await PrepareReferencesAsync(
                        request.OwnerId,
                        snapshot,
                        retainedReferences,
                        composer,
                        project,
                        panel,
                        cancellationToken);
                    var prompt = selectedCharacters.Length == 0
                        ? panel.EffectivePrompt
                        : $"Use the reference sheet only as identity guidance for {string.Join(", ", selectedCharacters)}. Preserve their identity and create this new scene: {panel.EffectivePrompt}";
                    var slot = layout.Slots[panelIndex];
                    workItems.Add(new PanelWorkItem(
                        panelIndex,
                        panel,
                        prompt!,
                        referenceSheet,
                        slot.ColumnSpan > slot.RowSpan));
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    panel.Status = PanelGenerationStatus.Failed;
                    panel.ImageBlobName = null;
                    panel.Issue = GetPanelIssue(exception);
                    panel.Error = GenerationResult.GetPanelError(panel);
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                    await NotifyCompletedAsync(request, panel.Number, CancellationToken.None);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            using var persistenceLock = new SemaphoreSlim(1, 1);
            await coordinator.RunAsync(
                workItems,
                "comic-panel",
                async (workItem, token) =>
                {
                    await NotifyStartedAsync(request, workItem.Panel.Number, token);
                    byte[]? image = null;
                    PanelGenerationIssue? issue = null;
                    try
                    {
                        image = await coordinator.ExecuteWithRetryAsync(
                            "comic-panel",
                            async retryToken =>
                            {
                                if (workItem.Panel.Issue is not null)
                                {
                                    await SaveIssueAsync(null, retryToken);
                                }
                                var imageTrace = new AiGenerationTrace { Operation = "panel-image", AttemptId = attempt.Id };
                                await persistenceLock.WaitAsync(retryToken);
                                try
                                {
                                    workItem.Panel.Traces.Add(imageTrace);
                                    await dbContext.SaveChangesAsync(retryToken);
                                }
                                finally
                                {
                                    persistenceLock.Release();
                                }
                                return await client.GenerateAsync(
                                    workItem.Prompt,
                                    workItem.ReferenceSheet,
                                    workItem.Landscape,
                                    retryToken,
                                    imageTrace);
                            },
                            IsTransient,
                            IsThrottled,
                            token,
                            async (exception, retryToken) =>
                            {
                                if (IsThrottled(exception))
                                {
                                    await SaveIssueAsync(PanelGenerationIssue.ProviderThrottled, retryToken);
                                }
                            });
                        var blobName = ArtifactPathBuilder.GetGenerationBlobName(
                            request.OwnerId,
                            Guid.Parse(document.Id),
                            $"panel-{workItem.Panel.Number:00}.png");
                        await UploadPngAsync(blobName, image, token);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        issue = GetPanelIssue(exception);
                    }

                    await persistenceLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (image is not null && issue is null)
                        {
                            panelImages[workItem.Index] = image;
                            workItem.Panel.ImageBlobName = ArtifactPathBuilder.GetGenerationBlobName(
                                request.OwnerId,
                                Guid.Parse(document.Id),
                                $"panel-{workItem.Panel.Number:00}.png");
                            workItem.Panel.Status = PanelGenerationStatus.Completed;
                            workItem.Panel.Error = null;
                            workItem.Panel.Issue = null;
                        }
                        else
                        {
                            workItem.Panel.Status = PanelGenerationStatus.Failed;
                            workItem.Panel.ImageBlobName = null;
                            workItem.Panel.Issue = issue;
                            workItem.Panel.Error = GenerationResult.GetPanelError(workItem.Panel);
                        }

                        document.UpdatedAt = DateTimeOffset.UtcNow;
                        await dbContext.SaveChangesAsync(CancellationToken.None);
                    }
                    finally
                    {
                        persistenceLock.Release();
                    }

                    await NotifyCompletedAsync(request, workItem.Panel.Number, CancellationToken.None);

                    async Task SaveIssueAsync(PanelGenerationIssue? currentIssue, CancellationToken retryToken)
                    {
                        await persistenceLock.WaitAsync(retryToken);
                        try
                        {
                            workItem.Panel.Issue = currentIssue;
                            workItem.Panel.Error = GenerationResult.GetPanelError(workItem.Panel);
                            document.UpdatedAt = DateTimeOffset.UtcNow;
                            await dbContext.SaveChangesAsync(retryToken);
                        }
                        finally
                        {
                            persistenceLock.Release();
                        }
                    }
                },
                cancellationToken);

            var board = await composer.BuildBoardAsync(
                snapshot?.Title ?? document.Title,
                panelImages,
                template,
                cancellationToken);
            document.BoardBlobName = ArtifactPathBuilder.GetGenerationBlobName(
                request.OwnerId,
                Guid.Parse(document.Id),
                "board.png");
            await UploadPngAsync(document.BoardBlobName, board, cancellationToken);
            document.Status = GenerationStatus.Completed;
            attempt.Status = "completed";
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            document.Status = GenerationStatus.Pending;
            attempt.Status = "canceled";
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            foreach (var panel in document.Panels.Where(panel => panel.Status == PanelGenerationStatus.Processing))
            {
                panel.Status = PanelGenerationStatus.Pending;
                panel.Issue = null;
                panel.Error = null;
            }
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            document.Status = GenerationStatus.Failed;
            attempt.Status = "failed";
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            document.Error = "The generation could not be completed.";
            document.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var panel in document.Panels.Where(panel => panel.Status == PanelGenerationStatus.Processing))
            {
                panel.Status = PanelGenerationStatus.Failed;
                panel.Issue = PanelGenerationIssue.TechnicalFailure;
                panel.Error = GenerationResult.GetPanelError(panel);
            }
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, byte[]>> LoadSnapshotReferencesAsync(
        GenerationDocument document,
        GenerationInputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var references = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var reference in snapshot.Characters.SelectMany(character => character.References))
        {
            var expectedPath = ArtifactPathBuilder.GetGenerationBlobName(
                document.OwnerId, Guid.Parse(document.Id), $"input-{reference.Id}-{reference.ContentSha256}");
            if (reference.BlobName != expectedPath || reference.ContentSha256.Length != 64)
            {
                throw new InvalidDataException("A retained reference has an invalid artifact path.");
            }
            if (references.ContainsKey(reference.BlobName))
            {
                continue;
            }
            var bytes = (await _container.GetBlobClient(reference.BlobName)
                .DownloadContentAsync(cancellationToken)).Value.Content.ToArray();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reference.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A retained reference failed its content-version check.");
            }
            references.Add(reference.BlobName, bytes);
        }
        return references;
    }

    private async Task<(string[] Characters, byte[]? ReferenceSheet)> PrepareReferencesAsync(
        string ownerId,
        GenerationInputSnapshot? snapshot,
        IReadOnlyDictionary<string, byte[]> retainedReferences,
        ImageComposer composer,
        ProjectDocument project,
        GenerationPanel panel,
        CancellationToken cancellationToken)
    {
        if (snapshot is not null)
        {
            var input = snapshot.Panels.Single(item => item.Number == panel.Number);
            var characters = snapshot.Characters.Where(character => input.CharacterIds.Contains(character.Id)).ToArray();
            if (characters.Length != input.CharacterIds.Distinct().Count())
            {
                throw new InvalidDataException("A selected snapshot character is missing.");
            }
            var streams = characters.SelectMany(character => character.References)
                .DistinctBy(reference => reference.Id)
                .Select(reference => (Stream)new MemoryStream(retainedReferences[reference.BlobName], writable: false))
                .ToArray();
            try
            {
                return (characters.Select(character => character.Name).ToArray(),
                    streams.Length == 0 ? null : await composer.BuildReferenceSheetAsync(streams, cancellationToken));
            }
            finally
            {
                foreach (var stream in streams)
                {
                    await stream.DisposeAsync();
                }
            }
        }
        var selectedCharacters = project.Characters
            .Where(character => panel.CharacterIds.Contains(character.Id, StringComparer.Ordinal))
            .ToArray();
        var missingCharacterId = panel.CharacterIds
            .FirstOrDefault(characterId => selectedCharacters.All(
                character => !string.Equals(character.Id, characterId, StringComparison.Ordinal)));
        if (missingCharacterId is not null)
        {
            throw new InvalidOperationException(
                $"Character '{missingCharacterId}' was not found in project '{project.Id}'.");
        }

        var referenceStreams = new List<Stream>();
        try
        {
            foreach (var referenceId in selectedCharacters
                         .SelectMany(character => character.References)
                         .Select(reference => reference.Id)
                         .Distinct(StringComparer.Ordinal))
            {
                var stream = await referenceService.OpenReadAsync(ownerId, referenceId, cancellationToken)
                    ?? throw new InvalidOperationException($"Reference '{referenceId}' was not found.");
                referenceStreams.Add(stream);
            }

            var referenceSheet = referenceStreams.Count == 0
                ? null
                : await composer.BuildReferenceSheetAsync(referenceStreams, cancellationToken);
            return (selectedCharacters.Select(character => character.Name).ToArray(), referenceSheet);
        }
        finally
        {
            foreach (var stream in referenceStreams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private async Task NotifyStartedAsync(
        BoardGenerationRequestMessage request,
        int panelNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            await notifier.NotifyStartedAsync(request.OwnerId, request.BoardId, panelNumber, cancellationToken, request.CommandId);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not publish the started notification for panel {PanelNumber}.", panelNumber);
        }
    }

    private async Task NotifyCompletedAsync(
        BoardGenerationRequestMessage request,
        int panelNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            await notifier.NotifyCompletedAsync(request.OwnerId, request.BoardId, panelNumber, cancellationToken, request.CommandId);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not publish the completed notification for panel {PanelNumber}.", panelNumber);
        }
    }

    internal static PanelGenerationIssue GetPanelIssue(Exception exception) => exception switch
    {
        FoundryContentSafetyException => PanelGenerationIssue.ContentSafetyRejected,
        FoundryGenerationException { Status: 429 } => PanelGenerationIssue.ProviderThrottled,
        _ => PanelGenerationIssue.TechnicalFailure
    };

    private static bool IsTransient(Exception exception) =>
        exception is TimeoutException ||
        exception is OperationCanceledException ||
        exception is FoundryGenerationException { Status: 408 or 429 or >= 500 } ||
        exception is FoundryGenerationException { InnerException: HttpRequestException };

    private static bool IsThrottled(Exception exception) =>
        exception is FoundryGenerationException { Status: 429 };

    private async Task UploadPngAsync(string blobName, byte[] bytes, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(blobName);
        await blob.UploadAsync(BinaryData.FromBytes(bytes), overwrite: true, cancellationToken);
        await blob.SetHttpHeadersAsync(
            new BlobHttpHeaders { ContentType = "image/png" },
            cancellationToken: cancellationToken);
    }

    private static void Validate(BoardGenerationRequestMessage request)
    {
        if (request.SchemaVersion is not (2 or 3 or 4))
        {
            throw new InvalidDataException($"Unsupported board generation message schema {request.SchemaVersion}.");
        }

        if (!Guid.TryParse(request.BoardId, out _) ||
            !Guid.TryParse(request.ProjectId, out _) ||
            string.IsNullOrWhiteSpace(request.OwnerId))
        {
            throw new InvalidDataException("The board generation message has invalid identifiers.");
        }
        if (request.SchemaVersion is 3 or 4 && request.RunId != request.BoardId)
        {
            throw new InvalidDataException("The board generation message has an invalid run identifier.");
        }
        if (request.SchemaVersion == 4 &&
            (request.Operation is not ("panel" or "compose") ||
             !Guid.TryParse(request.CommandId, out var commandId) || commandId == Guid.Empty ||
             request.PanelNumber is not > 0 || request.Revision is not > 0))
        {
            throw new InvalidDataException("The panel command message is invalid.");
        }
    }

    private sealed record PanelWorkItem(
        int Index,
        GenerationPanel Panel,
        string Prompt,
        byte[]? ReferenceSheet,
        bool Landscape);
}