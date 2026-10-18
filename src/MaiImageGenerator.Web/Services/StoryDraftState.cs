using MaiImageGenerator.Web.Models;

namespace MaiImageGenerator.Web.Services;

public sealed class StoryDraftState
{
    private readonly Dictionary<Guid, PreparedComic> drafts = [];

    public void Store(Guid projectId, PreparedComic draft) => drafts[projectId] = draft;

    public PreparedComic? Take(Guid projectId) => drafts.Remove(projectId, out var draft) ? draft : null;
}

public sealed record PreparedComic(BoardTemplate Template, ProjectScaffoldResponse Scaffold);
