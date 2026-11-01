using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using MaiImageGenerator.Web.Models;
using MaiImageGenerator.Web.Pages;
using MaiImageGenerator.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;

namespace MaiImageGenerator.Tests;

public sealed class SceneProgressRenderingTests
{
    [Fact]
    public async Task PanelCompletion_RendersSceneWhileBoardIsStillProcessing()
    {
        var id = Guid.NewGuid();
        using var client = Client((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/panels/1", StringComparison.Ordinal)
                ? ImageResponse(1)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Progress(id, 1)) }));
        using var services = Services(client);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestHome? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestHome>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestHome.Prepare)] = (Action<TestHome>)(home =>
                {
                    component = home;
                    home.PrepareBoard(id);
                })
            })));

        await renderer.Dispatcher.InvokeAsync(() => component!.CompletePanelAsync(id, 1));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);

        Assert.Contains("data:image/png;base64,AQ==", html);
        Assert.Contains("1 / 6 cases illustr", html);
        Assert.DoesNotContain("mai-comic-board.png", html);
        Assert.True(component!.IsGenerating);
    }

    [Fact]
    public async Task Refresh_RendersFirstImageBeforeAnotherImageFinishesDownloading()
    {
        var id = Guid.NewGuid();
        var secondRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/panels/2", StringComparison.Ordinal))
            {
                secondRequested.SetResult();
                await releaseSecond.Task.WaitAsync(token);
                return ImageResponse(2);
            }
            return request.RequestUri.AbsolutePath.EndsWith("/panels/1", StringComparison.Ordinal)
                ? ImageResponse(1)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Progress(id, 2)) };
        });
        using var services = Services(client);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestHome? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestHome>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestHome.Prepare)] = (Action<TestHome>)(home =>
                {
                    component = home;
                    home.PrepareBoard(id);
                })
            })));

        var refresh = renderer.Dispatcher.InvokeAsync(() => component!.RefreshAsync());
        try
        {
            await secondRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            Assert.Contains("data:image/png;base64,AQ==", html);
            Assert.DoesNotContain("data:image/png;base64,Ag==", html);
            Assert.False(refresh.IsCompleted);
            Assert.True(component!.IsGenerating);
        }
        finally
        {
            releaseSecond.TrySetResult();
            await refresh;
        }
        Assert.Contains("data:image/png;base64,Ag==",
            await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    }

    private static GenerationResponse Progress(Guid id, int completed) => new(
        id, "Test board", null, null, DateTimeOffset.UtcNow, false, BoardTemplate.Standard,
        [], GenerationStatus.Processing, 6, completed,
        Enumerable.Range(1, 6).Select(number => new PanelGenerationResponse(
            number, $"Scene {number}", [],
            number <= completed ? PanelGenerationStatus.Completed : PanelGenerationStatus.Processing,
            null, number <= completed ? new Uri($"api/generations/{id}/panels/{number}", UriKind.Relative) : null,
            null)).ToArray(), null, null);

    [Fact]
    public async Task PartialResult_ReopensWithSafeDiagnosisAndAvailableDownloads()
    {
        var id = Guid.NewGuid();
        using var client = Client((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("/panels/", StringComparison.Ordinal)
                ? ImageResponse(1)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Partial(id)) }));
        using var services = Services(client);
        for (var open = 0; open < 2; open++)
        {
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            TestHome? component = null;
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestHome>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(TestHome.Prepare)] = (Action<TestHome>)(home => component = home)
                })));

            await renderer.Dispatcher.InvokeAsync(() => component!.OpenAsync(id));
            var preview = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            Assert.Contains("partiel", preview);
            Assert.Contains("4 illustr", preview);
            Assert.Contains("2 en", preview);
            Assert.Contains("6 au total", preview);
            Assert.Contains("contenu", preview);
            Assert.Contains("technique", preview);
            Assert.DoesNotContain("private-provider-response", preview);
            Assert.DoesNotContain("Prête à lire", preview);

            await renderer.Dispatcher.InvokeAsync(() => component!.SelectAsync(0));
            var scenes = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            Assert.Contains("data:image/png;base64,AQ==", scenes);
            Assert.Contains("download=\"mai-comic-panel-1.png\"", scenes);
            Assert.False(component!.IsGenerating);

            await renderer.Dispatcher.InvokeAsync(() => component.StartPanelAsync(id, 6));
            Assert.False(component.IsGenerating);
            Assert.Equal(0, component.SelectedPanel);
            Assert.Contains("partiel", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        }
    }

    [Fact]
    public async Task Polling_RecoversPartialResultWithoutNotifications()
    {
        var id = Guid.NewGuid();
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/panels/", StringComparison.Ordinal))
                return Task.FromResult(ImageResponse(1));
            fetched.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Partial(id)) });
        });
        using var services = Services(client);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestHome? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestHome>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestHome.Prepare)] = (Action<TestHome>)(home =>
                {
                    component = home;
                    home.PrepareBoard(id);
                    home.BeginPolling();
                })
            })));

        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("partiel", html);
        Assert.Contains("4 illustr", html);
        Assert.False(component!.IsGenerating);
    }

    [Theory]
    [InlineData(GenerationStatus.Pending, null, "attente dans la file")]
    [InlineData(GenerationStatus.Processing, PanelGenerationIssue.ProviderThrottled, "limite les requ")]
    [InlineData(GenerationStatus.Completed, PanelGenerationIssue.ContentSafetyRejected, "curit")]
    [InlineData(GenerationStatus.Failed, PanelGenerationIssue.TechnicalFailure, "technique")]
    public async Task Refresh_DistinguishesDurableQueueThrottleSafetyAndTechnicalStates(
        GenerationStatus status, PanelGenerationIssue? issue, string expected)
    {
        var id = Guid.NewGuid();
        var response = Progress(id, 0) with
        {
            Status = status,
            Panels = Progress(id, 0).Panels.Select(panel => panel with
            {
                Status = status == GenerationStatus.Pending ? PanelGenerationStatus.Pending :
                    status == GenerationStatus.Processing ? PanelGenerationStatus.Processing : PanelGenerationStatus.Failed,
                Issue = issue,
                Error = "private-provider-response"
            }).ToArray(),
            FailedPanelCount = status is GenerationStatus.Completed or GenerationStatus.Failed ? 6 : 0,
            Completeness = GenerationCompleteness.None
        };
        using var client = Client((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) }));
        using var services = Services(client);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestHome? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestHome>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestHome.Prepare)] = (Action<TestHome>)(home => { component = home; home.PrepareBoard(id); })
            })));
        await renderer.Dispatcher.InvokeAsync(() => component!.RefreshAsync());
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains(expected, html);
        Assert.DoesNotContain("private-provider-response", html);
        if (status is GenerationStatus.Completed or GenerationStatus.Failed)
            Assert.Contains("Aucune image utilisable", html);
    }

    [Fact]
    public async Task ArtifactFailure_DoesNotHideOtherImagesOrChangeSavedCounts()
    {
        var id = Guid.NewGuid();
        using var client = Client((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/panels/1", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : request.RequestUri.AbsolutePath.Contains("/panels/", StringComparison.Ordinal)
                    ? ImageResponse(2)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Partial(id)) }));
        using var services = Services(client);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestHome? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestHome>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestHome.Prepare)] = (Action<TestHome>)(home => { component = home; home.PrepareBoard(id); })
            })));
        await renderer.Dispatcher.InvokeAsync(() => component!.RefreshAsync());
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("data:image/png;base64,Ag==", html);
        Assert.Contains("4 illustr", html);
        Assert.Contains("2 en", html);
        Assert.True(component!.HasPendingDownloads);
    }

    [Theory]
    [InlineData(4, "partiel")]
    [InlineData(6, "complet")]
    public async Task LegacyResponseWithoutNewFields_DerivesCompletenessAndKeepsBoardDownload(
        int completed, string expected)
    {
        var id = Guid.NewGuid();
        var result = Progress(id, completed) with
        {
            Status = GenerationStatus.Completed,
            Panels = Progress(id, completed).Panels.Select(panel => panel.Number <= completed ? panel :
                panel with { Status = PanelGenerationStatus.Failed }).ToArray(),
            BoardUri = new Uri($"api/generations/{id}/board", UriKind.Relative)
        };
        using var client = Client((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/board", StringComparison.Ordinal) ||
            request.RequestUri.AbsolutePath.Contains("/panels/", StringComparison.Ordinal)
                ? ImageResponse(1)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) }));
        using var services = Services(client);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestHome? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestHome>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestHome.Prepare)] = (Action<TestHome>)(home =>
                {
                    component = home;
                    home.PrepareBoard(id);
                    home.IncludeBoard(result);
                })
            })));

        await renderer.Dispatcher.InvokeAsync(() => component!.RefreshAsync());
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains(expected, html);
        Assert.Contains("download=\"mai-comic-board.png\"", html);
        Assert.False(component!.IsGenerating);
        Assert.False(component.HasPendingDownloads);
        Assert.Equal(6 - completed, component.SavedFailedPanelCount);
    }

    private static GenerationResponse Partial(Guid id) => Progress(id, 4) with
    {
        Status = GenerationStatus.Completed,
        Panels = Progress(id, 4).Panels.Select(panel => panel.Number <= 4 ? panel : panel with
        {
            Status = PanelGenerationStatus.Failed,
            Issue = panel.Number == 5 ? PanelGenerationIssue.ContentSafetyRejected : PanelGenerationIssue.TechnicalFailure,
            Error = "private-provider-response"
        }).ToArray(),
        FailedPanelCount = 2,
        Completeness = GenerationCompleteness.Partial
    };

    private static HttpResponseMessage ImageResponse(byte value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([value])
        {
            Headers = { ContentType = new("image/png") }
        }
    };

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("https://example.test/") };

    private static ServiceProvider Services(HttpClient client) => new ServiceCollection()
        .AddLogging()
        .AddSingleton(new GenerationApiClient(client))
        .AddSingleton<StoryDraftState>()
        .AddSingleton<IAccessTokenProvider, UnusedTokenProvider>()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
        .AddSingleton<NavigationManager, TestNavigation>()
        .AddSingleton<NotificationService>()
        .AddSingleton<DialogService>()
        .AddSingleton<IJSRuntime, UnusedJsRuntime>()
        .BuildServiceProvider();

    public sealed class TestHome : Home
    {
        [Parameter] public Action<TestHome> Prepare { get; set; } = default!;
        public bool IsGenerating => (bool)Field("isGenerating").GetValue(this)!;
        public int SelectedPanel => (int)Field("selectedPanel").GetValue(this)!;
        public bool HasPendingDownloads => (bool)typeof(Home).GetProperty("HasPendingArtifacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public int SavedFailedPanelCount => ((List<GenerationSummaryResponse>)Field("projectBoards").GetValue(this)!)[0].FailedPanelCount;
        protected override Task OnInitializedAsync() { Prepare(this); return Task.CompletedTask; }
        protected override Task OnParametersSetAsync() => Task.CompletedTask;
        public void PrepareBoard(Guid id)
        {
            Field("activeGenerationId").SetValue(this, id);
            Field("isBoardLoading").SetValue(this, false);
            Field("isGenerating").SetValue(this, true);
        }
        public Task RefreshAsync() => (Task)Method("RefreshGenerationAsync").Invoke(this, null)!;
        public void IncludeBoard(GenerationResponse result) =>
            ((List<GenerationSummaryResponse>)Field("projectBoards").GetValue(this)!)
                .Add(new(result.GenerationId, result.Title, result.CreatedAt, result.Status, null));
        public async Task OpenAsync(Guid id) { GenerationId = id; await base.OnParametersSetAsync(); StateHasChanged(); }
        public void SelectAsync(int index) { Method("SelectPanel").Invoke(this, [index]); StateHasChanged(); }
        public void BeginPolling() =>
            Field("statusRefreshTask").SetValue(this, Method("RefreshActiveGenerationPeriodicallyAsync").Invoke(this, null));
        public Task StartPanelAsync(Guid id, int number)
        {
            var notificationType = typeof(Home).GetNestedType("PanelNotification", BindingFlags.NonPublic)!;
            var notification = Activator.CreateInstance(notificationType, id.ToString("D"), number)!;
            return (Task)Method("HandlePanelStartedAsync").Invoke(this, [notification])!;
        }
        public Task CompletePanelAsync(Guid id, int number)
        {
            var notificationType = typeof(Home).GetNestedType("PanelNotification", BindingFlags.NonPublic)!;
            var notification = Activator.CreateInstance(notificationType, id.ToString("D"), number)!;
            return (Task)Method("HandlePanelCompletedAsync").Invoke(this, [notification])!;
        }
        private static FieldInfo Field(string name) =>
            typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static MethodInfo Method(string name) =>
            typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://example.test/", "https://example.test/boards/new");
        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedTokenProvider : IAccessTokenProvider
    {
        public ValueTask<AccessTokenResult> RequestAccessToken() => throw new NotSupportedException();
        public ValueTask<AccessTokenResult> RequestAccessToken(AccessTokenRequestOptions options) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) =>
            throw new NotSupportedException();
    }
}
