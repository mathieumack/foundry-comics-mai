using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Services;

namespace MaiImageGenerator.Tests;

public sealed class BoardSubmissionLeaseTests
{
    [Fact]
    public void ActiveSubmission_BlocksCompetingSubmissionAndDeletion_UntilReleased()
    {
        var project = new ProjectDocument();
        var now = DateTimeOffset.UtcNow;
        var id = BoardSubmissionLease.Acquire(project, now);
        Assert.True(BoardSubmissionLease.IsActive(project, now));
        Assert.Throws<ArgumentException>(() => BoardSubmissionLease.Acquire(project, now));
        BoardSubmissionLease.Release(project, "another-submission");
        Assert.True(BoardSubmissionLease.IsActive(project, now));
        BoardSubmissionLease.Release(project, id);
        Assert.False(BoardSubmissionLease.IsActive(project, now));
        Assert.Null(project.BoardSubmissionStartedAt);
    }

    [Fact]
    public void CrashedSubmission_ExpiresAfterItsBoundedCaptureDeadline()
    {
        var project = new ProjectDocument();
        var now = DateTimeOffset.UtcNow;
        var previous = BoardSubmissionLease.Acquire(project, now);
        Assert.True(BoardSubmissionLease.SubmissionTimeout < BoardSubmissionLease.Lifetime);
        Assert.False(BoardSubmissionLease.IsActive(project, now + BoardSubmissionLease.Lifetime));
        var next = BoardSubmissionLease.Acquire(project, now + BoardSubmissionLease.Lifetime);
        Assert.NotEqual(previous, next);
        BoardSubmissionLease.Release(project, previous);
        Assert.Equal(next, project.BoardSubmissionId);
    }

    [Fact]
    public void DeletedProject_AndLegacyProject_DoNotInventActiveSubmissions()
    {
        var project = new ProjectDocument();
        Assert.False(BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow));
        project.DeletionRequestedAt = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => BoardSubmissionLease.Acquire(project, DateTimeOffset.UtcNow));
    }
}
