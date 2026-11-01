using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Shared.Services;

public static class BoardSubmissionLease
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SubmissionTimeout = TimeSpan.FromMinutes(3);

    public static bool IsActive(ProjectDocument project, DateTimeOffset now) =>
        project.BoardSubmissionId is not null &&
        project.BoardSubmissionStartedAt is { } started &&
        now - started < Lifetime;

    public static string Acquire(ProjectDocument project, DateTimeOffset now)
    {
        if (project.DeletionRequestedAt is not null || IsActive(project, now))
        {
            throw new ArgumentException("The project is being deleted or another board submission is in progress.");
        }
        project.BoardSubmissionId = Guid.NewGuid().ToString("D");
        project.BoardSubmissionStartedAt = now;
        return project.BoardSubmissionId;
    }

    public static void Release(ProjectDocument project, string submissionId)
    {
        if (project.BoardSubmissionId == submissionId)
        {
            project.BoardSubmissionId = null;
            project.BoardSubmissionStartedAt = null;
        }
    }
}
