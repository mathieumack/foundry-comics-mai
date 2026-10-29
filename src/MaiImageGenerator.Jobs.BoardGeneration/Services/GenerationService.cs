using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class GenerationService(
    MaiImageGeneratorDbContext dbContext,
    ReferenceService referenceService,
    BlobServiceClient blobServiceClient,
    FoundryImageClient foundryClient,
    ImageComposer imageComposer,
    PanelGenerationNotifier notifier,
    ImageGenerationCoordinator imageGeneration,
    ILogger<GenerationService> logger)
{
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("generations");

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

        if (document.ProjectId != project.Id)
        {
            throw new InvalidOperationException(
                $"Generation '{request.BoardId}' does not belong to project '{request.ProjectId}'.");
        }

        if (document.Status is GenerationStatus.Completed or GenerationStatus.Canceled ||
            project.DeletionRequestedAt is not null)
        {
            return;
        }

        document.Status = GenerationStatus.Processing;
        document.SchemaVersion = 3;
        document.Error = null;
        foreach (var failedPanel in document.Panels.Where(panel => panel.Status == PanelGenerationStatus.Failed))
        {
            failedPanel.Status = PanelGenerationStatus.Pending;
            failedPanel.Error = null;
        }
        document.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var panelImages = new byte[]?[document.Panels.Count];
            var panelIndexesToGenerate = new List<int>();
            var layout = BoardTemplateDefinitions.Resolve(document.Template, document.Panels.Count);
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
                panelIndexesToGenerate.Add(panelIndex);
            }

            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            var workItems = new List<PanelWorkItem>(panelIndexesToGenerate.Count);
            foreach (var panelIndex in panelIndexesToGenerate)
            {
                var panel = document.Panels[panelIndex];
                try
                {
                    if (request.RewritePrompt)
                    {
                        var rewriteTrace = new AiGenerationTrace { Operation = "prompt-rewrite" };
                        panel.Traces.Add(rewriteTrace);
                        panel.EffectivePrompt = await foundryClient.RewritePromptAsync(
                            document.Context,
                            panel.Prompt,
                            cancellationToken,
                            rewriteTrace);
                    }
                    else
                    {
                        panel.EffectivePrompt = panel.Prompt;
                    }
                    var (selectedCharacters, referenceSheet) = await PrepareReferencesAsync(
                        request.OwnerId,
                        project,
                        panel,
                        cancellationToken);
                    var prompt = selectedCharacters.Length == 0
                        ? panel.EffectivePrompt
                        : $"Use the reference sheet only as identity guidance for {string.Join(", ", selectedCharacters.Select(item => item.Name))}. Preserve their identity and create this new scene: {panel.EffectivePrompt}";
                    var slot = layout.Slots[panelIndex];
                    var imageTrace = new AiGenerationTrace { Operation = "panel-image" };
                    panel.Traces.Add(imageTrace);
                    workItems.Add(new PanelWorkItem(
                        panelIndex,
                        panel,
                        prompt!,
                        referenceSheet,
                        slot.ColumnSpan > slot.RowSpan,
                        imageTrace));
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    panel.Status = PanelGenerationStatus.Failed;
                    panel.ImageBlobName = null;
                    panel.Error = GetPanelError(exception);
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                    await NotifyCompletedAsync(request, panel.Number, CancellationToken.None);
                }
            }

            using var persistenceLock = new SemaphoreSlim(1, 1);
            await imageGeneration.RunAsync(
                workItems,
                "comic-panel",
                async (workItem, token) =>
                {
                    await NotifyStartedAsync(request, workItem.Panel.Number, token);
                    byte[]? image = null;
                    string? error = null;
                    try
                    {
                        image = await imageGeneration.ExecuteWithRetryAsync(
                            "comic-panel",
                            retryToken => foundryClient.GenerateAsync(
                                workItem.Prompt,
                                workItem.ReferenceSheet,
                                workItem.Landscape,
                                retryToken,
                                workItem.Trace),
                            IsTransient,
                            IsThrottled,
                            token);
                        var blobName = ArtifactPathBuilder.GetGenerationBlobName(
                            request.OwnerId,
                            Guid.Parse(document.Id),
                            $"panel-{workItem.Panel.Number:00}.png");
                        await UploadPngAsync(blobName, image, token);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        error = GetPanelError(exception);
                    }

                    await persistenceLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (image is not null && error is null)
                        {
                            panelImages[workItem.Index] = image;
                            workItem.Panel.ImageBlobName = ArtifactPathBuilder.GetGenerationBlobName(
                                request.OwnerId,
                                Guid.Parse(document.Id),
                                $"panel-{workItem.Panel.Number:00}.png");
                            workItem.Panel.Status = PanelGenerationStatus.Completed;
                            workItem.Panel.Error = null;
                        }
                        else
                        {
                            workItem.Panel.Status = PanelGenerationStatus.Failed;
                            workItem.Panel.ImageBlobName = null;
                            workItem.Panel.Error = error;
                        }

                        document.UpdatedAt = DateTimeOffset.UtcNow;
                        await dbContext.SaveChangesAsync(CancellationToken.None);
                    }
                    finally
                    {
                        persistenceLock.Release();
                    }

                    await NotifyCompletedAsync(request, workItem.Panel.Number, CancellationToken.None);
                },
                cancellationToken);

            var board = await imageComposer.BuildBoardAsync(
                document.Title,
                panelImages,
                document.Template,
                cancellationToken);
            document.BoardBlobName = ArtifactPathBuilder.GetGenerationBlobName(
                request.OwnerId,
                Guid.Parse(document.Id),
                "board.png");
            await UploadPngAsync(document.BoardBlobName, board, cancellationToken);
            document.Status = GenerationStatus.Completed;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            document.Status = GenerationStatus.Pending;
            foreach (var panel in document.Panels.Where(panel => panel.Status == PanelGenerationStatus.Processing))
            {
                panel.Status = PanelGenerationStatus.Pending;
            }
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            document.Status = GenerationStatus.Failed;
            document.Error = "The generation could not be completed.";
            document.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var panel in document.Panels.Where(panel => panel.Status == PanelGenerationStatus.Processing))
            {
                panel.Status = PanelGenerationStatus.Failed;
                panel.Error = exception.GetType().Name;
            }
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<(ProjectCharacter[] Characters, byte[]? ReferenceSheet)> PrepareReferencesAsync(
        string ownerId,
        ProjectDocument project,
        GenerationPanel panel,
        CancellationToken cancellationToken)
    {
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
                : await imageComposer.BuildReferenceSheetAsync(referenceStreams, cancellationToken);
            return (selectedCharacters, referenceSheet);
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
            await notifier.NotifyStartedAsync(request.OwnerId, request.BoardId, panelNumber, cancellationToken);
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
            await notifier.NotifyCompletedAsync(request.OwnerId, request.BoardId, panelNumber, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not publish the completed notification for panel {PanelNumber}.", panelNumber);
        }
    }

    private static string GetPanelError(Exception exception) =>
        exception is FoundryContentSafetyException ? exception.Message : exception.GetType().Name;

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
        if (request.SchemaVersion != 2)
        {
            throw new InvalidDataException($"Unsupported board generation message schema {request.SchemaVersion}.");
        }

        if (!Guid.TryParse(request.BoardId, out _) ||
            !Guid.TryParse(request.ProjectId, out _) ||
            string.IsNullOrWhiteSpace(request.OwnerId))
        {
            throw new InvalidDataException("The board generation message has invalid identifiers.");
        }
    }

    private sealed record PanelWorkItem(
        int Index,
        GenerationPanel Panel,
        string Prompt,
        byte[]? ReferenceSheet,
        bool Landscape,
        AiGenerationTrace Trace);
}