using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
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
    ILogger<GenerationService> logger)
{
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("generations");

    public async Task ProcessAsync(
        BoardGenerationRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var operationScope = logger.BeginScope(
            "OperationId:{OperationId} GenerationId:{GenerationId} ProjectId:{ProjectId}",
            request.BoardId,
            request.BoardId,
            request.ProjectId);
        var operationStopwatch = Stopwatch.StartNew();
        var generationStage = "Validation";
        logger.LogInformation(
            "Async operation {OperationName} started at stage {GenerationStage}.",
            "GenerateBoard",
            generationStage);
        Validate(request);
        generationStage = "DatabaseLoad";
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

        if (document.Status == GenerationStatus.Completed)
        {
            logger.LogInformation(
                "Async operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                "GenerateBoard",
                "AlreadyCompleted",
                operationStopwatch.ElapsedMilliseconds);
            return;
        }

        generationStage = "DatabasePersistProcessing";
        document.Status = GenerationStatus.Processing;
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
            var panelImages = new List<byte[]?>(document.Panels.Count);
            var layout = BoardTemplateDefinitions.Resolve(document.Template, document.Panels.Count);
            for (var panelIndex = 0; panelIndex < document.Panels.Count; panelIndex++)
            {
                var panel = document.Panels[panelIndex];
                using var panelScope = logger.BeginScope("PanelNumber:{PanelNumber}", panel.Number);
                var panelStopwatch = Stopwatch.StartNew();
                generationStage = "PanelLoadOrGenerate";
                logger.LogInformation(
                    "Async operation {OperationName} started at stage {GenerationStage}.",
                    "GeneratePanel",
                    generationStage);
                if (panel.Status == PanelGenerationStatus.Completed && panel.ImageBlobName is not null)
                {
                    try
                    {
                        panelImages.Add((await _container.GetBlobClient(panel.ImageBlobName)
                            .DownloadContentAsync(cancellationToken)).Value.Content.ToArray());
                        await notifier.NotifyCompletedAsync(
                            request.OwnerId,
                            request.BoardId,
                            panel.Number,
                            cancellationToken);
                        logger.LogInformation(
                            "Async operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                            "GeneratePanel",
                            "Reused",
                            panelStopwatch.ElapsedMilliseconds);
                        continue;
                    }
                    catch (RequestFailedException exception) when (exception.Status == 404)
                    {
                        logger.LogWarning(
                            exception,
                            "Async dependency {DependencyType} completed with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                            "BlobStorage",
                            "NotFoundRegenerate",
                            "PanelDownload",
                            panelStopwatch.ElapsedMilliseconds);
                        panel.Status = PanelGenerationStatus.Pending;
                    }
                }

                panel.Status = PanelGenerationStatus.Processing;
                panel.Error = null;
                generationStage = "PromptRewrite";
                panel.EffectivePrompt = request.RewritePrompt
                    ? await foundryClient.RewritePromptAsync(document.Context, panel.Prompt, cancellationToken)
                    : panel.Prompt;

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
                var generationStarted = false;
                try
                {
                    generationStage = "ReferenceDownload";
                    foreach (var referenceId in selectedCharacters
                                 .SelectMany(character => character.References)
                                 .Select(reference => reference.Id)
                                 .Distinct(StringComparer.Ordinal))
                    {
                        var stream = await referenceService.OpenReadAsync(request.OwnerId, referenceId, cancellationToken)
                            ?? throw new InvalidOperationException($"Reference '{referenceId}' was not found.");
                        referenceStreams.Add(stream);
                    }

                    var referenceSheet = referenceStreams.Count == 0
                        ? null
                        : await imageComposer.BuildReferenceSheetAsync(referenceStreams, cancellationToken);
                    var prompt = selectedCharacters.Length == 0
                        ? panel.EffectivePrompt
                        : $"Use the reference sheet only as identity guidance for {string.Join(", ", selectedCharacters.Select(item => item.Name))}. Preserve their identity and create this new scene: {panel.EffectivePrompt}";
                    var slot = layout.Slots[panelIndex];
                    var landscape = slot.ColumnSpan > slot.RowSpan;
                    await notifier.NotifyStartedAsync(
                        request.OwnerId,
                        request.BoardId,
                        panel.Number,
                        cancellationToken);
                    generationStarted = true;
                    generationStage = "ImageGeneration";
                    var image = await foundryClient.GenerateAsync(
                        prompt!,
                        referenceSheet,
                        landscape,
                        cancellationToken);
                    panelImages.Add(image);
                    panel.ImageBlobName = ArtifactPathBuilder.GetGenerationBlobName(
                        request.OwnerId,
                        Guid.Parse(document.Id),
                        $"panel-{panel.Number:00}.png");
                    generationStage = "PanelUpload";
                    await UploadPngAsync(panel.ImageBlobName, image, cancellationToken);
                    panel.Status = PanelGenerationStatus.Completed;
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    generationStage = "DatabasePersistPanel";
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogInformation(
                        "Async operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                        "GeneratePanel",
                        "Succeeded",
                        panelStopwatch.ElapsedMilliseconds);
                }
                catch (FoundryContentSafetyException exception)
                {
                    panelImages.Add(null);
                    panel.Status = PanelGenerationStatus.Failed;
                    panel.ImageBlobName = null;
                    panel.Error = exception.Message;
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogWarning(
                        exception,
                        "Async operation {OperationName} completed with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                        "GeneratePanel",
                        "ContentSafetyRejected",
                        "ImageGeneration",
                        panelStopwatch.ElapsedMilliseconds);
                }
                finally
                {
                    foreach (var stream in referenceStreams)
                    {
                        await stream.DisposeAsync();
                    }

                    if (generationStarted)
                    {
                        await notifier.NotifyCompletedAsync(
                            request.OwnerId,
                            request.BoardId,
                            panel.Number,
                            CancellationToken.None);
                    }
                }
            }

            generationStage = "BoardComposition";
            var board = await imageComposer.BuildBoardAsync(
                document.Title,
                panelImages,
                document.Template,
                cancellationToken);
            document.BoardBlobName = ArtifactPathBuilder.GetGenerationBlobName(
                request.OwnerId,
                Guid.Parse(document.Id),
                "board.png");
            generationStage = "BoardUpload";
            await UploadPngAsync(document.BoardBlobName, board, cancellationToken);
            document.Status = GenerationStatus.Completed;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            generationStage = "DatabasePersistCompleted";
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Async operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                "GenerateBoard",
                "Succeeded",
                operationStopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException exception)
        {
            document.Status = GenerationStatus.Pending;
            var currentPanel = document.Panels.FirstOrDefault(panel => panel.Status == PanelGenerationStatus.Processing);
            if (currentPanel is not null)
            {
                currentPanel.Status = PanelGenerationStatus.Pending;
            }
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(
                exception,
                "Async operation {OperationName} ended with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                "GenerateBoard",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                generationStage,
                operationStopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            document.Status = GenerationStatus.Failed;
            document.Error = "The generation could not be completed.";
            document.UpdatedAt = DateTimeOffset.UtcNow;
            var currentPanel = document.Panels.FirstOrDefault(panel => panel.Status == PanelGenerationStatus.Processing);
            if (currentPanel is not null)
            {
                currentPanel.Status = PanelGenerationStatus.Failed;
                currentPanel.Error = exception.GetType().Name;
            }
            await dbContext.SaveChangesAsync(CancellationToken.None);
            logger.LogError(
                exception,
                "Async operation {OperationName} failed at stage {GenerationStage} with code {SafeErrorCode} after {ElapsedMs} ms.",
                "GenerateBoard",
                generationStage,
                exception.GetType().Name,
                operationStopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private async Task UploadPngAsync(string blobName, byte[] bytes, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Async dependency {DependencyType} started at stage {GenerationStage}.",
            "BlobStorage",
            "ImageUpload");
        var blob = _container.GetBlobClient(blobName);
        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(bytes), overwrite: true, cancellationToken);
            await blob.SetHttpHeadersAsync(
                new BlobHttpHeaders { ContentType = "image/png" },
                cancellationToken: cancellationToken);
            logger.LogInformation(
                "Async dependency {DependencyType} completed with outcome {Outcome} in {ElapsedMs} ms.",
                "BlobStorage",
                "Succeeded",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "Async dependency {DependencyType} ended with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                "BlobStorage",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                "ImageUpload",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Async dependency {DependencyType} failed at stage {GenerationStage} after {ElapsedMs} ms.",
                "BlobStorage",
                "ImageUpload",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
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
}