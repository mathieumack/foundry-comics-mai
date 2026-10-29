using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaiImageGenerator.Tests;

public sealed class ProjectDeletionServiceTests
{
    private const string OwnerId = "owner-a";

    [Fact]
    public async Task DeleteAsync_DeletesComicArtifactsAndRecordsWithProjectLast()
    {
        var projectId = Guid.NewGuid();
        var generationId = Guid.NewGuid();
        var referenceId = Guid.NewGuid().ToString("N");
        var deletion = CreateDeletion(projectId, ProjectType.Comic, generationId, referenceId);
        var repository = new FakeRepository(OwnerId, deletion);
        var artifacts = new FakeArtifactStore();
        var service = CreateService(repository, artifacts);

        var deleted = await service.DeleteAsync(OwnerId, projectId, CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal(["load", "generations", "references", "project"], repository.Calls);
        Assert.Equal(OwnerId, artifacts.OwnerId);
        Assert.Equal(projectId, artifacts.ProjectId);
        Assert.Equal([generationId], artifacts.GenerationIds);
        Assert.Equal([referenceId], artifacts.ReferenceIds);
    }

    [Fact]
    public async Task DeleteAsync_DeletesBookProjectWithoutComicGenerations()
    {
        var projectId = Guid.NewGuid();
        var deletion = CreateDeletion(projectId, ProjectType.Book);
        var repository = new FakeRepository(OwnerId, deletion);
        var artifacts = new FakeArtifactStore();
        var service = CreateService(repository, artifacts);

        var deleted = await service.DeleteAsync(OwnerId, projectId, CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal(projectId, artifacts.ProjectId);
        Assert.Empty(artifacts.GenerationIds);
        Assert.Equal(["load", "generations", "references", "project"], repository.Calls);
    }

    [Fact]
    public async Task DeleteAsync_IncludesProjectAndBoardScaffoldSidecars()
    {
        var projectId = Guid.NewGuid();
        var generationId = Guid.NewGuid();
        var projectScaffoldId = Guid.NewGuid();
        var boardScaffoldId = Guid.NewGuid();
        var deletion = CreateDeletion(projectId, ProjectType.Comic, generationId);
        deletion.Project.ScaffoldId = projectScaffoldId;
        deletion.Generations[0].ScaffoldId = boardScaffoldId;
        var artifacts = new FakeArtifactStore();
        var service = CreateService(new FakeRepository(OwnerId, deletion), artifacts);

        await service.DeleteAsync(OwnerId, projectId, CancellationToken.None);

        Assert.Equal([generationId, boardScaffoldId, projectScaffoldId], artifacts.GenerationIds);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotCleanProjectOutsideAuthenticatedOwner()
    {
        var projectId = Guid.NewGuid();
        var repository = new FakeRepository(OwnerId, CreateDeletion(projectId, ProjectType.Comic));
        var artifacts = new FakeArtifactStore();
        var service = CreateService(repository, artifacts);

        var deleted = await service.DeleteAsync("owner-b", projectId, CancellationToken.None);

        Assert.False(deleted);
        Assert.Equal(["load"], repository.Calls);
        Assert.Equal(0, artifacts.CallCount);
    }

    [Fact]
    public async Task DeleteAsync_TreatsMissingArtifactsAsSuccessfulCleanup()
    {
        var projectId = Guid.NewGuid();
        var repository = new FakeRepository(OwnerId, CreateDeletion(projectId, ProjectType.Comic));
        var artifacts = new FakeArtifactStore();
        var service = CreateService(repository, artifacts);

        var deleted = await service.DeleteAsync(OwnerId, projectId, CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal(1, artifacts.CallCount);
        Assert.Equal("project", repository.Calls[^1]);
    }

    [Fact]
    public async Task DeleteAsync_StorageFailureLeavesRecordsAndCanBeRetried()
    {
        var projectId = Guid.NewGuid();
        var repository = new FakeRepository(OwnerId, CreateDeletion(projectId, ProjectType.Comic));
        var artifacts = new FakeArtifactStore { FailuresRemaining = 1 };
        var service = CreateService(repository, artifacts);

        await Assert.ThrowsAsync<ProjectDeletionException>(
            () => service.DeleteAsync(OwnerId, projectId, CancellationToken.None));
        Assert.Equal(["load"], repository.Calls);

        var deleted = await service.DeleteAsync(OwnerId, projectId, CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal(2, artifacts.CallCount);
        Assert.Equal(["load", "load", "generations", "references", "project"], repository.Calls);
    }

    private static ProjectDeletionService CreateService(
        IProjectDeletionRepository repository,
        IProjectArtifactStore artifacts) =>
        new(repository, artifacts, NullLogger<ProjectDeletionService>.Instance);

    private static ProjectDeletionData CreateDeletion(
        Guid projectId,
        ProjectType type,
        Guid? generationId = null,
        string? referenceId = null)
    {
        var project = new ProjectDocument
        {
            Id = projectId.ToString("D"),
            OwnerId = OwnerId,
            Type = type
        };
        var generations = generationId is null
            ? []
            : new List<GenerationDocument>
            {
                new()
                {
                    Id = generationId.Value.ToString("D"),
                    OwnerId = OwnerId,
                    ProjectId = project.Id
                }
            };
        var references = referenceId is null
            ? []
            : new List<ReferenceDocument>
            {
                new() { Id = referenceId, OwnerId = OwnerId }
            };
        return new(
            project,
            generations,
            references,
            referenceId is null ? [] : [referenceId]);
    }

    private sealed class FakeRepository(string expectedOwnerId, ProjectDeletionData deletion)
        : IProjectDeletionRepository
    {
        public List<string> Calls { get; } = [];

        public Task<ProjectDeletionData?> LoadAsync(
            string ownerId,
            Guid projectId,
            CancellationToken cancellationToken)
        {
            Calls.Add("load");
            return Task.FromResult<ProjectDeletionData?>(
                ownerId == expectedOwnerId && projectId.ToString("D") == deletion.Project.Id
                    ? deletion
                    : null);
        }

        public Task DeleteGenerationsAsync(
            IReadOnlyList<GenerationDocument> generations,
            CancellationToken cancellationToken)
        {
            Calls.Add("generations");
            return Task.CompletedTask;
        }

        public Task DeleteReferencesAsync(
            IReadOnlyList<ReferenceDocument> references,
            CancellationToken cancellationToken)
        {
            Calls.Add("references");
            return Task.CompletedTask;
        }

        public Task DeleteProjectAsync(ProjectDocument project, CancellationToken cancellationToken)
        {
            Calls.Add("project");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArtifactStore : IProjectArtifactStore
    {
        public int CallCount { get; private set; }
        public int FailuresRemaining { get; set; }
        public string? OwnerId { get; private set; }
        public Guid ProjectId { get; private set; }
        public IReadOnlyList<Guid> GenerationIds { get; private set; } = [];
        public IReadOnlyList<string> ReferenceIds { get; private set; } = [];

        public Task DeleteAsync(
            string ownerId,
            Guid projectId,
            IReadOnlyList<Guid> generationIds,
            IReadOnlyList<string> referenceIds,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OwnerId = ownerId;
            ProjectId = projectId;
            GenerationIds = generationIds;
            ReferenceIds = referenceIds;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new IOException("Storage is unavailable.");
            }

            return Task.CompletedTask;
        }
    }
}
