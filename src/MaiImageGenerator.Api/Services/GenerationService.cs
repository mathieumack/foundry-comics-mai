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
    QueueClient jobsQueue)
{
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("generations");

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

        await LoadReferencesAsync(ownerId, request, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var document = new GenerationDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = ownerId,
            Title = request.Title.Trim(),
            Context = request.Context?.Trim(),
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

        try
        {
            var message = new BoardGenerationRequestMessage
            {
                BoardId = document.Id,
                OwnerId = ownerId,
                RewritePrompt = request.RewritePrompt,
                Characters = request.Characters.Select(character => new BoardGenerationCharacter(
                    character.Id,
                    character.Name,
                    character.ReferenceIds)).ToArray()
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

        return ToResponse(document);
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
        return document is null ? null : ToResponse(document);
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

    private async Task LoadReferencesAsync(
        string ownerId,
        CreateGenerationRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var referenceId in request.Characters.SelectMany(character => character.ReferenceIds).Distinct())
        {
            _ = await referenceService.GetAsync(ownerId, referenceId, cancellationToken)
                ?? throw new ArgumentException($"Reference '{referenceId}' was not found.");
        }
    }

    private static GenerationResponse ToResponse(GenerationDocument document) => new(
        Guid.Parse(document.Id),
        document.Title,
        document.Context,
        document.CreatedAt,
        document.Status,
        document.Panels.Count,
        document.Panels.Count(panel => panel.Status == PanelGenerationStatus.Completed),
        document.Panels.Select(panel => new PanelGenerationResponse(
            panel.Number,
            panel.Prompt,
            panel.Status,
            panel.EffectivePrompt,
            panel.ImageBlobName is null ? null : new Uri($"api/generations/{document.Id}/panels/{panel.Number}", UriKind.Relative))).ToArray(),
        document.BoardBlobName is null ? null : new Uri($"api/generations/{document.Id}/board", UriKind.Relative),
        document.Error);

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
}