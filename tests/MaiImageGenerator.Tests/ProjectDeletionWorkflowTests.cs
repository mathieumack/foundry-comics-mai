using MaiImageGenerator.Web.Models;

namespace MaiImageGenerator.Tests;

public sealed class ProjectDeletionWorkflowTests
{
    [Theory]
    [InlineData(ProjectType.Comic, "bande dessinée")]
    [InlineData(ProjectType.Book, "livre illustré")]
    public async Task RunAsync_RequiresExplicitPermanentDeletionConfirmation(
        ProjectType type,
        string expectedFormat)
    {
        var project = CreateProject(type);
        ProjectDeletionConfirmation? shown = null;
        var deleteCalled = false;

        var deleted = await ProjectDeletionWorkflow.RunAsync(
            project,
            confirmation =>
            {
                shown = confirmation;
                return Task.FromResult(false);
            },
            _ =>
            {
                deleteCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(deleted);
        Assert.False(deleteCalled);
        Assert.NotNull(shown);
        Assert.Contains("définitivement", shown.Message, StringComparison.Ordinal);
        Assert.Contains("irréversible", shown.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFormat, shown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DeletesOnlyAfterConfirmation()
    {
        var deleteCalled = false;

        var deleted = await ProjectDeletionWorkflow.RunAsync(
            CreateProject(ProjectType.Comic),
            _ => Task.FromResult(true),
            _ =>
            {
                deleteCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(deleted);
        Assert.True(deleteCalled);
    }

    private static ProjectResponse CreateProject(ProjectType type) => new(
        Guid.NewGuid(),
        "Mon histoire",
        null,
        type,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        []);
}
