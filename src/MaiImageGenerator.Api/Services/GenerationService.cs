using Azure.Storage.Blobs;
using Azure.Messaging.ServiceBus;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Api.Services;

public sealed class GenerationService(
    MaiImageGeneratorDbContext dbContext,
    ReferenceService referenceService,
    BlobServiceClient blobServiceClient,
    ServiceBusSender jobsQueue,
    ScaffoldTraceStore scaffoldTraceStore,
    GenerationSnapshotStore snapshotStore,
    ILogger<GenerationService> logger)
{
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("generations");

    public async Task<GenerationResponse> CreateBoardAsync(
        string ownerId,
        ProjectDocument project,
        CreateBoardRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = GenerationRequestValidator.Validate(
            request,
            project.Characters.Select(character => character.Id).ToHashSet(StringComparer.Ordinal));
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", validationErrors.SelectMany(item => item.Value)));
        }

        var scaffoldTraces = await scaffoldTraceStore.ReadAsync(ownerId, request.ScaffoldId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var document = new GenerationDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = ownerId,
            ProjectId = project.Id,
            SchemaVersion = 4,
            ScaffoldId = request.ScaffoldId,
            ScaffoldTraces = scaffoldTraces,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            Context = project.Context,
            RewritePrompt = request.RewritePrompt,
            Template = request.Template ?? BoardTemplate.Standard,
            Status = GenerationStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Panels = request.Panels.Select((panel, index) => new GenerationPanel
            {
                Number = index + 1,
                Prompt = panel.Prompt.Trim(),
                CharacterIds = panel.CharacterIds.Distinct().ToList(),
                Status = PanelGenerationStatus.Pending
            }).ToList()
        };

        await SubmitAsync(document, project, cancellationToken);
        return ToResponse(document, project);
    }

    public async Task<GenerationResponse> GenerateAsync(
        string ownerId,
        CreateGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = GenerationRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", validationErrors.SelectMany(item => item.Value)));
        }

        var references = await LoadReferencesAsync(ownerId, request, cancellationToken);
        var scaffoldTraces = await scaffoldTraceStore.ReadAsync(ownerId, request.ScaffoldId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var project = new ProjectDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = ownerId,
            Title = request.Title.Trim(),
            Context = request.Context?.Trim(),
            Characters = request.Characters.Select(character => new ProjectCharacter
            {
                Id = character.Id,
                Name = character.Name.Trim(),
                References = character.ReferenceIds.Select(referenceId => new ProjectCharacterReference
                {
                    Id = referenceId,
                    FileName = references[referenceId].FileName
                }).ToList()
            }).ToList(),
            CreatedAt = now,
            UpdatedAt = now
        };
        var document = new GenerationDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = ownerId,
            ProjectId = project.Id,
            SchemaVersion = 4,
            ScaffoldId = request.ScaffoldId,
            ScaffoldTraces = scaffoldTraces,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            Context = request.Context?.Trim(),
            RewritePrompt = request.RewritePrompt,
            Template = request.Template ?? BoardTemplate.Standard,
            Status = GenerationStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Panels = request.Panels.Select((panel, index) => new GenerationPanel
            {
                Number = index + 1,
                Prompt = panel.Prompt.Trim(),
                CharacterIds = panel.CharacterIds.Distinct().ToList(),
                Status = PanelGenerationStatus.Pending
            }).ToList()
        };

        dbContext.Projects.Add(project);
        await SubmitAsync(document, project, cancellationToken);

        return ToResponse(document, project);
    }

    public async Task<GenerationResponse?> GetAsync(
        string ownerId,
        Guid generationId,
        CancellationToken cancellationToken)
    {
        var id = generationId.ToString("D");
        var document = await dbContext.Generations
            .WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (document is null)
        {
            return null;
        }

        if (document.InputSnapshot is not null)
        {
            return ToResponse(document, new ProjectDocument());
        }

        var project = await dbContext.Projects
            .WithPartitionKey(ownerId)
            .SingleAsync(
                item => item.Id == document.ProjectId && item.OwnerId == ownerId,
                cancellationToken);
        return ToResponse(document, project);
    }

    public async Task<IReadOnlyList<GenerationSummaryResponse>> ListAsync(
        string ownerId,
        CancellationToken cancellationToken)
    {
        var documents = await dbContext.Generations
            .WithPartitionKey(ownerId)
            .Where(item => item.OwnerId == ownerId)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        return documents.Select(ToSummaryResponse).ToArray();
    }

    public async Task<IReadOnlyList<GenerationSummaryResponse>> ListAsync(
        string ownerId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var documents = await dbContext.Generations
            .WithPartitionKey(ownerId)
            .Where(item => item.OwnerId == ownerId && item.ProjectId == projectId)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        return documents.Select(ToSummaryResponse).ToArray();
    }

    public async Task<(Stream Content, string ContentType)?> OpenArtifactAsync(
        string ownerId,
        Guid generationId,
        int? panelNumber,
        CancellationToken cancellationToken)
    {
        var id = generationId.ToString("D");
        var document = await dbContext.Generations
            .WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        var blobName = panelNumber is null
            ? document?.BoardBlobName
            : document?.Panels.SingleOrDefault(panel => panel.Number == panelNumber)?.ImageBlobName;
        return blobName is null
            ? null
            : (await _container.GetBlobClient(blobName).OpenReadAsync(cancellationToken: cancellationToken), "image/png");
    }

    public async Task<(Stream Content, string ContentType)?> OpenReferenceAsync(
        string ownerId,
        Guid generationId,
        string referenceId,
        CancellationToken cancellationToken)
    {
        var id = generationId.ToString("D");
        var document = await dbContext.Generations
            .WithPartitionKey(ownerId)
            .SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        var reference = document?.InputSnapshot?.Characters.SelectMany(character => character.References)
            .FirstOrDefault(item => item.Id == referenceId);
        return reference is null ? null :
            (await snapshotStore.OpenReferenceAsync(ownerId, generationId, reference, cancellationToken), reference.ContentType);
    }

    private async Task<IReadOnlyDictionary<string, ReferenceDocument>> LoadReferencesAsync(
        string ownerId,
        CreateGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var references = new Dictionary<string, ReferenceDocument>(StringComparer.Ordinal);
        foreach (var referenceId in request.Characters.SelectMany(character => character.ReferenceIds).Distinct())
        {
            var reference = await referenceService.GetAsync(ownerId, referenceId, cancellationToken)
                ?? throw new ArgumentException($"Reference '{referenceId}' was not found.");
            references[referenceId] = reference;
        }

        return references;
    }

    private async Task SubmitAsync(
        GenerationDocument document,
        ProjectDocument project,
        CancellationToken cancellationToken)
    {
        if (project.OwnerId != document.OwnerId)
        {
            throw new ArgumentException("The generation does not belong to this owner.");
        }
        var submissionId = BoardSubmissionLease.Acquire(project, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BoardSubmissionLease.SubmissionTimeout);
        try
        {
            dbContext.Generations.Add(document);
            await dbContext.SaveChangesAsync(timeout.Token);
            var selectedIds = document.Panels.SelectMany(panel => panel.CharacterIds).ToHashSet(StringComparer.Ordinal);
            var references = new Dictionary<string, ReferenceDocument>(StringComparer.Ordinal);
            foreach (var referenceId in project.Characters.Where(character => selectedIds.Contains(character.Id))
                         .SelectMany(character => character.References).Select(reference => reference.Id).Distinct())
            {
                references[referenceId] = await referenceService.GetAsync(document.OwnerId, referenceId, timeout.Token)
                    ?? throw new ArgumentException("A selected reference was not found.");
            }
            document.InputSnapshot = await snapshotStore.CaptureAsync(document, project, references, timeout.Token);
            await dbContext.SaveChangesAsync(timeout.Token);
            var message = new BoardGenerationRequestMessage
            {
                SchemaVersion = 3,
                BoardId = document.Id,
                OwnerId = document.OwnerId,
                ProjectId = project.Id,
                RewritePrompt = document.InputSnapshot.RewritePrompt,
                RunId = document.InputSnapshot.RunId
            };
            await jobsQueue.SendMessageAsync(
                new ServiceBusMessage(BinaryData.FromObjectAsJson(message))
                {
                    MessageId = document.Id,
                    ContentType = "application/json"
                },
                timeout.Token);
        }
        catch
        {
            document.Status = GenerationStatus.Failed;
            document.Error = document.InputSnapshot is null
                ? "The generation inputs could not be retained."
                : "The generation could not be queued.";
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            BoardSubmissionLease.Release(project, submissionId);
            try
            {
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                // Never overwrite concurrent project edits to clear a lease; it will expire.
                logger.LogWarning(exception, "Could not release the board submission lease; deletion remains protected until expiry.");
            }
        }
    }

    public static GenerationResponse ToResponse(GenerationDocument document, ProjectDocument project)
    {
        var result = GenerationResult.From(document);
        var snapshot = document.InputSnapshot;
        var characters = snapshot is null
            ? project.Characters.Select(character => new CharacterGenerationResponse(
                character.Id, character.Name, character.References.Select(reference =>
                    new CharacterReferenceResponse(reference.Id, reference.FileName)).ToArray())).ToArray()
            : snapshot.Characters.Select(character => new CharacterGenerationResponse(
                character.Id, character.Name, character.References.Select(reference =>
                    new CharacterReferenceResponse(reference.Id, reference.FileName,
                        new Uri($"api/generations/{document.Id}/references/{reference.Id}/content", UriKind.Relative)))
                    .ToArray())).ToArray();
        return new(
        Guid.Parse(document.Id),
        snapshot?.Title ?? document.Title,
        snapshot is null ? document.Description : snapshot.Description,
        snapshot is null ? document.Context : snapshot.Context,
        document.CreatedAt,
        snapshot?.RewritePrompt ?? document.RewritePrompt,
        snapshot?.Template ?? document.Template,
        characters,
        document.Status,
        result.PanelCount,
        result.CompletedPanelCount,
        document.Panels.Select(panel => new PanelGenerationResponse(
            panel.Number,
            snapshot?.Panels.Single(input => input.Number == panel.Number).Prompt ?? panel.Prompt,
            (snapshot?.Panels.Single(input => input.Number == panel.Number).CharacterIds ?? panel.CharacterIds)
                .Where(characterId => characters.Any(character => character.Id == characterId))
                .ToArray(),
            panel.Status,
            panel.EffectivePrompt,
            panel.ImageBlobName is null ? null : new Uri($"api/generations/{document.Id}/panels/{panel.Number}", UriKind.Relative),
            GenerationResult.GetPanelError(panel),
            panel.Traces.ToArray(),
            GenerationResult.GetIssue(panel))).ToArray(),
        document.BoardBlobName is null ? null : new Uri($"api/generations/{document.Id}/board", UriKind.Relative),
        document.Error,
        document.ScaffoldTraces.ToArray(),
        result.FailedPanelCount,
        result.Completeness,
        snapshot is null ? null : new GenerationRunResponse(
            snapshot.RunId, snapshot.Version, snapshot.AcceptedAt,
            snapshot.PromptTemplateVersion, snapshot.LayoutVersion, snapshot.Settings,
            document.Attempts.ToArray(),
            snapshot.Characters.SelectMany(character => character.References)
                .DistinctBy(reference => reference.Id)
                .Select(reference => new GenerationReferenceVersionResponse(reference.Id, reference.ContentSha256))
                .ToArray()));
    }

    public static GenerationSummaryResponse ToSummaryResponse(GenerationDocument document)
    {
        var result = GenerationResult.From(document);
        var thumbnailPanel = document.Panels
            .Where(panel => panel.ImageBlobName is not null)
            .OrderBy(panel => panel.Number)
            .FirstOrDefault();
        return new(
            Guid.Parse(document.Id),
            document.Title,
            document.CreatedAt,
            document.Status,
            thumbnailPanel is null
                ? null
                : new Uri($"api/generations/{document.Id}/panels/{thumbnailPanel.Number}", UriKind.Relative),
            result.PanelCount,
            result.CompletedPanelCount,
            result.FailedPanelCount,
            result.Completeness);
    }
}