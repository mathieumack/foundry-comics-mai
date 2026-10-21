using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Shared.Services;

public static class BookImageGenerationLease
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(25);

    public static bool IsActive(ProjectDocument project, DateTimeOffset now) =>
        project.BookPlan?.ImageGenerationRequestId is not null &&
        project.BookPlan.ImageGenerationStartedAt > now - Duration;

    public static void EnsureInactive(ProjectDocument project)
    {
        if (IsActive(project, DateTimeOffset.UtcNow))
        {
            throw new ArgumentException(
                "Chapter images are currently being generated. Retry after the image batch completes.");
        }
    }
}
