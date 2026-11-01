using System.Security.Cryptography;
using Azure.Storage.Blobs;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using MaiImageGenerator.Shared.Services;
using Microsoft.Extensions.Options;

namespace MaiImageGenerator.Api.Services;

public sealed class GenerationSnapshotStore(BlobServiceClient blobs, IOptions<FoundryOptions> options)
{
    public async Task<Stream> OpenReferenceAsync(
        string ownerId,
        Guid generationId,
        GenerationInputReference reference,
        CancellationToken cancellationToken)
    {
        var expectedPath = ArtifactPathBuilder.GetGenerationBlobName(
            ownerId, generationId, $"input-{reference.Id}-{reference.ContentSha256}");
        if (reference.BlobName != expectedPath || reference.ContentSha256.Length != 64)
        {
            throw new InvalidDataException("The retained reference has an invalid artifact path.");
        }
        var content = (await blobs.GetBlobContainerClient("generations").GetBlobClient(expectedPath)
            .DownloadContentAsync(cancellationToken)).Value.Content;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(content.ToMemory().Span)),
                reference.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The retained reference failed its content-version check.");
        }
        return content.ToStream();
    }

    public async Task<GenerationInputSnapshot> CaptureAsync(
        GenerationDocument document,
        ProjectDocument project,
        IReadOnlyDictionary<string, ReferenceDocument> references,
        CancellationToken cancellationToken)
    {
        if (document.OwnerId != project.OwnerId || document.ProjectId != project.Id)
        {
            throw new ArgumentException("The generation does not belong to this project.");
        }

        var settings = options.Value;
        if (settings.ImageSize != 1024 || string.IsNullOrWhiteSpace(settings.PromptModel) ||
            string.IsNullOrWhiteSpace(settings.ImageModel))
        {
            throw new InvalidOperationException("The board generation model settings are invalid.");
        }
        _ = new ImageGenerationCoordinator(settings);
        var selectedIds = document.Panels.SelectMany(panel => panel.CharacterIds).ToHashSet(StringComparer.Ordinal);
        var characters = project.Characters.Where(character => selectedIds.Contains(character.Id)).ToArray();
        if (characters.Length != selectedIds.Count)
        {
            throw new ArgumentException("A selected character was not found.");
        }

        var retained = new Dictionary<string, GenerationInputReference>(StringComparer.Ordinal);
        foreach (var referenceId in characters.SelectMany(character => character.References)
                     .Select(reference => reference.Id).Distinct(StringComparer.Ordinal))
        {
            if (!references.TryGetValue(referenceId, out var reference) ||
                reference.OwnerId != document.OwnerId ||
                !reference.BlobName.StartsWith(
                    ArtifactPathBuilder.GetReferencePrefix(document.OwnerId, referenceId) + ".",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException("A selected reference was not found for this owner.");
            }

            var content = (await blobs.GetBlobContainerClient("references").GetBlobClient(reference.BlobName)
                .DownloadContentAsync(cancellationToken)).Value.Content;
            var hash = Convert.ToHexString(SHA256.HashData(content.ToMemory().Span)).ToLowerInvariant();
            var blobName = ArtifactPathBuilder.GetGenerationBlobName(
                document.OwnerId, Guid.Parse(document.Id), $"input-{referenceId}-{hash}");
            await blobs.GetBlobContainerClient("generations").GetBlobClient(blobName)
                .UploadAsync(content, cancellationToken);
            retained.Add(referenceId, new GenerationInputReference
            {
                Id = referenceId,
                FileName = reference.FileName,
                BlobName = blobName,
                ContentType = reference.ContentType,
                ContentSha256 = hash
            });
        }

        return new GenerationInputSnapshot
        {
            RunId = document.Id,
            AcceptedAt = document.CreatedAt,
            Title = document.Title,
            Description = document.Description,
            Context = document.Context,
            RewritePrompt = document.RewritePrompt,
            Template = document.Template,
            PromptTemplateVersion = BoardInputVersions.PromptTemplate,
            LayoutVersion = BoardInputVersions.Layout,
            Settings = new GenerationModelSettings
            {
                PromptDeployment = settings.PromptModel,
                ImageDeployment = settings.ImageModel,
                PromptModelVersion = settings.PromptModelVersion,
                ImageModelVersion = settings.ImageModelVersion,
                ImageSize = settings.ImageSize,
                MaxParallelImages = settings.MaxParallelImages,
                ImageRetryCount = settings.ImageRetryCount,
                ImageRetryBaseDelayMilliseconds = settings.ImageRetryBaseDelayMilliseconds
            },
            Panels = document.Panels.Select(panel => new GenerationInputPanel
            {
                Number = panel.Number,
                Prompt = panel.Prompt,
                CharacterIds = panel.CharacterIds.ToList()
            }).ToList(),
            Characters = characters.Select(character => new GenerationInputCharacter
            {
                Id = character.Id,
                Name = character.Name,
                References = character.References.Select(reference =>
                {
                    var input = retained[reference.Id];
                    return new GenerationInputReference
                    {
                        Id = input.Id,
                        FileName = input.FileName,
                        BlobName = input.BlobName,
                        ContentType = input.ContentType,
                        ContentSha256 = input.ContentSha256
                    };
                }).ToList()
            }).ToList()
        };
    }
}
