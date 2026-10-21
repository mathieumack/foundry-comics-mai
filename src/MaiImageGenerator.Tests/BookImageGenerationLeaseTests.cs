using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Services;

namespace MaiImageGenerator.Tests;

public sealed class BookImageGenerationLeaseTests
{
    [Fact]
    public void IsActive_RequiresFreshPersistedOwnership()
    {
        var now = DateTimeOffset.UtcNow;
        var project = CreateProject("request", now - TimeSpan.FromMinutes(1));

        Assert.True(BookImageGenerationLease.IsActive(project, now));

        project.BookPlan!.ImageGenerationStartedAt = now - BookImageGenerationLease.Duration;
        Assert.False(BookImageGenerationLease.IsActive(project, now));

        project.BookPlan.ImageGenerationStartedAt = now;
        project.BookPlan.ImageGenerationRequestId = null;
        Assert.False(BookImageGenerationLease.IsActive(project, now));
    }

    [Fact]
    public void BatchTimeout_IsShorterThanLeaseDuration()
    {
        Assert.True(BookImageGenerationLease.BatchTimeout < BookImageGenerationLease.Duration);
    }

    private static ProjectDocument CreateProject(string requestId, DateTimeOffset startedAt) =>
        new()
        {
            Type = ProjectType.Book,
            BookPlan = new BookPlan
            {
                ImageGenerationRequestId = requestId,
                ImageGenerationStartedAt = startedAt
            }
        };
}
