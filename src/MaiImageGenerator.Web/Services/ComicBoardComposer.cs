using Microsoft.JSInterop;

namespace MaiImageGenerator.Web.Services;

public sealed class ComicBoardComposer(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private IJSObjectReference? module;

    public async Task<string> ComposeAsync(
        string title,
        IReadOnlyList<string> imageDataUrls,
        ComicBoardLayout? layout = null,
        CancellationToken cancellationToken = default)
    {
        if (imageDataUrls.Count == 0)
        {
            throw new ArgumentException("The comic board must contain at least one panel.", nameof(imageDataUrls));
        }

        module ??= await jsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            cancellationToken,
            "./js/comicBoard.js");

        return await module.InvokeAsync<string>(
            "composeComicBoard",
            cancellationToken,
            title.Trim(),
            imageDataUrls,
            layout ?? new ComicBoardLayout());
    }

    public async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            await module.DisposeAsync();
        }
    }
}

public sealed record ComicBoardLayout(
    int PanelSize = 1024,
    int Gutter = 24,
    int Margin = 24,
    int TitleHeight = 104);