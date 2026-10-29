using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Api.Services;

public sealed class GenerationService(
    MaiImageGeneratorDbContext dbContext,
    ReferenceService referenceService,
    BlobServiceClient blobServiceClient,
    QueueClient jobsQueue,
    ScaffoldTraceStore scaffoldTraceStore)
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
            SchemaVersion = 3,
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

        dbContext.Generations.Add(document);
        await dbContext.SaveChangesAsync(cancellationToken);
        await QueueAsync(document, project, cancellationToken);
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
            SchemaVersion = 3,
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
        dbContext.Generations.Add(document);
        await dbContext.SaveChangesAsync(cancellationToken);

        await QueueAsync(document, project, cancellationToken);

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

    private async Task<IReadOnlyDictionary<string, GenerationDocumentReference>> LoadReferencesAsync(
        string ownerId,
        CreateGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var references = new Dictionary<string, GenerationDocumentReference>(StringComparer.Ordinal);
        foreach (var referenceId in request.Characters.SelectMany(character => character.ReferenceIds).Distinct())
        {
            var reference = await referenceService.GetAsync(ownerId, referenceId, cancellationToken)
                ?? throw new ArgumentException($"Reference '{referenceId}' was not found.");
            references[referenceId] = new(reference.FileName);
        }

        return references;
    }

    private async Task QueueAsync(
        GenerationDocument document,
        ProjectDocument project,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = new BoardGenerationRequestMessage
            {
                BoardId = document.Id,
                OwnerId = document.OwnerId,
                ProjectId = project.Id,
                RewritePrompt = document.RewritePrompt
            };
            await jobsQueue.SendMessageAsync(BinaryData.FromObjectAsJson(message).ToString(), cancellationToken);
        }
        catch
        {
            document.Status = GenerationStatus.Failed;
            document.Error = "The generation could not be queued.";
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public static GenerationResponse ToResponse(GenerationDocument document, ProjectDocument project) => new(
        Guid.Parse(document.Id),
        document.Title,
        document.Description,
        document.Context,
        document.CreatedAt,
        document.RewritePrompt,
        document.Template,
        project.Characters.Select(character => new CharacterGenerationResponse(
            character.Id,
            character.Name,
            character.References.Select(reference => new CharacterReferenceResponse(
                reference.Id,
                reference.FileName)).ToArray())).ToArray(),
        document.Status,
        document.Panels.Count,
        document.Panels.Count(panel => panel.Status == PanelGenerationStatus.Completed),
        document.Panels.Select(panel => new PanelGenerationResponse(
            panel.Number,
            panel.Prompt,
            panel.CharacterIds
                .Where(characterId => project.Characters.Any(character => character.Id == characterId))
                .ToArray(),
            panel.Status,
            panel.EffectivePrompt,
            panel.ImageBlobName is null ? null : new Uri($"api/generations/{document.Id}/panels/{panel.Number}", UriKind.Relative),
            panel.Error,
            panel.Traces.ToArray())).ToArray(),
        document.BoardBlobName is null ? null : new Uri($"api/generations/{document.Id}/board", UriKind.Relative),
        document.Error,
        document.ScaffoldTraces.ToArray());

    private static GenerationSummaryResponse ToSummaryResponse(GenerationDocument document)
    {
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
                : new Uri($"api/generations/{document.Id}/panels/{thumbnailPanel.Number}", UriKind.Relative));
    }

    private sealed record GenerationDocumentReference(string FileName);
}