using System.Net;
using System.Reflection;
using MaiImageGenerator.Web.Components;
using MaiImageGenerator.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MaiImageGenerator.Tests;

public sealed class EpubDownloadTests
{
    [Fact]
    public async Task DownloadUsesAuthenticatedClientAndPassesBytesAndFilenameToBrowser()
    {
        var id = Guid.NewGuid();
        Uri? requested = null;
        using var client = Client(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([80, 75, 3, 4])
                {
                    Headers = { ContentType = new("application/epub+zip"), ContentDisposition = new("attachment") { FileNameStar = "My story.epub" } }
                }
            };
        });
        var js = new DownloadJsRuntime();
        using var services = Services(client, js);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestExportButton? button = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestExportButton>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestExportButton.ProjectId)] = id,
                [nameof(TestExportButton.Prepare)] = (Action<TestExportButton>)(component => button = component)
            })));

        await renderer.Dispatcher.InvokeAsync(() => button!.DownloadAsync());

        Assert.Equal($"/api/projects/{id:D}/export/epub", requested!.AbsolutePath);
        Assert.Equal("maiStudio.downloadFile", js.Identifier);
        Assert.Equal("My story.epub", js.FileName);
        Assert.Equal(new byte[] { 80, 75, 3, 4 }, js.Content);
        Assert.DoesNotContain("role=\"alert\"", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    }

    [Fact]
    public async Task DisabledActionDoesNotRequestDownload()
    {
        var requested = false;
        using var client = Client(_ => { requested = true; return new HttpResponseMessage(HttpStatusCode.OK); });
        using var services = Services(client, new DownloadJsRuntime());
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestExportButton? button = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestExportButton>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestExportButton.Disabled)] = true,
                [nameof(TestExportButton.Prepare)] = (Action<TestExportButton>)(component => button = component)
            })));
        await renderer.Dispatcher.InvokeAsync(() => button!.DownloadAsync());
        Assert.False(requested);
        Assert.Contains("disabled", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    }

    [Fact]
    public async Task ExportFailureShowsActionableErrorAndAllowsRetry()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("Generate chapter prose, then retry export.")
        });
        var js = new DownloadJsRuntime();
        using var services = Services(client, js);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestExportButton? button = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestExportButton>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestExportButton.Prepare)] = (Action<TestExportButton>)(component => button = component)
            })));
        await renderer.Dispatcher.InvokeAsync(() => button!.DownloadAsync());
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("Generate chapter prose", html);
        Assert.DoesNotContain("disabled", html);
        Assert.Null(js.Identifier);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new Handler(response)) { BaseAddress = new Uri("https://example.test/") };

    private static ServiceProvider Services(HttpClient client, IJSRuntime js) =>
        new ServiceCollection().AddLogging().AddSingleton(new GenerationApiClient(client)).AddSingleton(js).BuildServiceProvider();

    public sealed class TestExportButton : EpubExportButton
    {
        [Parameter] public Action<TestExportButton> Prepare { get; set; } = default!;
        protected override void OnInitialized() => Prepare(this);
        public async Task DownloadAsync()
        {
            await (Task)typeof(EpubExportButton).GetMethod("ExportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null)!;
            StateHasChanged();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class DownloadJsRuntime : IJSRuntime
    {
        public string? Identifier { get; private set; }
        public string? FileName { get; private set; }
        public byte[]? Content { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Identifier = identifier;
            FileName = Assert.IsType<string>(args![0]);
            var reference = Assert.IsType<DotNetStreamReference>(args[1]);
            using var output = new MemoryStream();
            await reference.Stream.CopyToAsync(output, cancellationToken);
            Content = output.ToArray();
            return default!;
        }
    }
}
