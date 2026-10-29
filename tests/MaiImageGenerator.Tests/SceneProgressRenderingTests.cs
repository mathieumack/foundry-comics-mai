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
        protected override Task OnInitializedAsync() { Prepare(this); return Task.CompletedTask; }
        protected override Task OnParametersSetAsync() => Task.CompletedTask;
        public void PrepareBoard(Guid id)
        {
            Field("activeGenerationId").SetValue(this, id);
            Field("isBoardLoading").SetValue(this, false);
            Field("isGenerating").SetValue(this, true);
        }
        public Task RefreshAsync() => (Task)Method("RefreshGenerationAsync").Invoke(this, null)!;
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
