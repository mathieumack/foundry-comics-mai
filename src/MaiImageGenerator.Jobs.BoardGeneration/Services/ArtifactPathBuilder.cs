using System.Security.Cryptography;
using System.Text;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public static class ArtifactPathBuilder
{
    public static string GetReferenceBlobName(string ownerId, string referenceId, string extension) =>
        $"{GetOwnerPrefix(ownerId)}/{referenceId}{extension}";

    public static string GetGenerationBlobName(string ownerId, Guid generationId, string fileName) =>
        $"{GetOwnerPrefix(ownerId)}/{generationId:N}/{Path.GetFileName(fileName)}";

    private static string GetOwnerPrefix(string ownerId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId))).ToLowerInvariant();
}