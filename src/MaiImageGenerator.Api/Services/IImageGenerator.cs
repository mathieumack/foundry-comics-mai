using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Api.Services;

public interface IImageGenerator
{
    Task<byte[]> GenerateAsync(string prompt, byte[]? referenceSheet, CancellationToken cancellationToken,
        AiGenerationTrace? trace = null);
}