using System.Security.Cryptography;
using System.Text;

namespace MaiImageGenerator.Api.Services;

public static class ArtifactPathBuilder
{
    public static string GetReferenceBlobName(string ownerId, string referenceId, string extension) =>
        $"{GetOwnerPrefix(ownerId)}/{referenceId}{extension}";

    public static string GetGenerationBlobName(string ownerId, Guid generationId, string fileName) =>
        $"{GetGenerationPrefix(ownerId, generationId)}{Path.GetFileName(fileName)}";

    public static string GetGenerationPrefix(string ownerId, Guid generationId) =>
        $"{GetOwnerPrefix(ownerId)}/{generationId:N}/";

    public static string GetReferencePrefix(string ownerId, string referenceId) =>
        $"{GetOwnerPrefix(ownerId)}/{Path.GetFileName(referenceId)}";

    private static string GetOwnerPrefix(string ownerId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId))).ToLowerInvariant();
}