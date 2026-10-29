using System.Text.Json;
using System.Text.RegularExpressions;
using MaiImageGenerator.Web.Components;
using MaiImageGenerator.Web.Models;
using MaiImageGenerator.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MaiImageGenerator.Tests;

public sealed class AiTraceFormattingTests
{
    [Fact]
    public void MissingUsageDoesNotInventTokens()
    {
        var trace = Trace() with { InputTokens = 400, TotalTokens = 500 };

        Assert.Equal("tokens unavailable", AiTraceFormatting.Usage(trace));
        Assert.Equal("unavailable", AiTraceFormatting.Details(trace).Single(item => item.Key == "Total tokens").Value);
    }

    [Theory]
    [InlineData(120L, null, "120 input tokens (provider-reported)")]
    [InlineData(null, 85L, "85 output tokens (provider-reported)")]
    [InlineData(120L, 85L, "120 input tokens · 85 output tokens (provider-reported)")]
    public void PartialUsageDoesNotManufactureTotal(long? input, long? output, string expected)
    {
        var trace = Trace() with { UsageSource = "provider-reported", InputTokens = input, OutputTokens = output };

        Assert.Equal(expected, AiTraceFormatting.Usage(trace));
        Assert.Equal("unavailable", AiTraceFormatting.Details(trace).Single(item => item.Key == "Total tokens").Value);
    }

    [Fact]
    public void EstimatedUsageIsNeverPresentedAsProviderReported()
    {
        var trace = Trace() with { UsageSource = "estimated", TotalTokens = 1234 };

        Assert.Equal("1,234 tokens (estimated)", AiTraceFormatting.Usage(trace));
        Assert.DoesNotContain("provider-reported", AiTraceFormatting.Summary(trace));
    }

    [Fact]
    public void LargeProviderCountsRemainExact()
    {
        var trace = Trace() with { UsageSource = "provider-reported", TotalTokens = 9_223_372_036_854_775_807 };

        Assert.Equal("9,223,372,036,854,775,807 tokens (provider-reported)", AiTraceFormatting.Usage(trace));
    }

    [Fact]
    public void ImageWithoutUsageShowsRequestFallbackAndUnavailableTokens()
    {
        var trace = Trace() with { Operation = "panel-image", Model = "MAI-Image-2" };

        Assert.Equal("MAI-Image-2 · 1 image · 1024×1024 · tokens unavailable", AiTraceFormatting.Summary(trace));
    }

    [Fact]
    public void FailedImageDoesNotClaimItWasProduced()
    {
        var trace = Trace() with { Operation = "chapter-image", Status = "failed", ImageSize = "1536x1024" };
        var summary = AiTraceFormatting.Summary(trace);

        Assert.Contains("failed", summary);
        Assert.Contains("1 image requested · 1536×1024", summary);
    }

    [Fact]
    public void FailedImageWithZeroProviderResultsDoesNotClaimZeroWereRequested()
    {
        var trace = Trace() with { Operation = "chapter-image", Status = "failed", ImageCount = 0 };
        var summary = AiTraceFormatting.Summary(trace);

        Assert.Contains("failed", summary);
        Assert.Contains("0 images reported · 1024×1024", summary);
        Assert.DoesNotContain("requested", summary);
    }

    [Fact]
    public void DetailsPreserveProviderMetadataAndMissingFields()
    {
        var started = DateTimeOffset.Parse("2026-10-29T19:00:00Z");
        var trace = Trace() with
        {
            Provider = "Azure AI Foundry", RequestId = "request-123", StartedAt = started,
            CompletedAt = started.AddSeconds(3), DurationMs = 3000, InputTokens = 100,
            UsageSource = "provider-reported", Operation = "chapter-image", ImageQuality = "high"
        };
        var details = AiTraceFormatting.Details(trace).ToDictionary();

        Assert.Equal("Azure AI Foundry", details["Provider"]);
        Assert.Equal("request-123", details["Request ID"]);
        Assert.Equal("100", details["Input tokens"]);
        Assert.Equal("unavailable", details["Output tokens"]);
        Assert.Equal("unavailable", details["Total tokens"]);
        Assert.Equal("provider-reported", details["Usage source"]);
        Assert.Equal("3,000 ms", details["Duration"]);
        Assert.Equal("high", details["Image quality"]);
        Assert.StartsWith("2026-10-29T19:00:00", details["Started"]);
        Assert.StartsWith("2026-10-29T19:00:03", details["Completed"]);
    }

    [Fact]
    public void MultipleRequestsRemainSeparateAndFilterByOperation()
    {
        AiGenerationTrace[] traces =
        [
            Trace() with { RequestId = "rewrite-1", UsageSource = "estimated", TotalTokens = 200 },
            Trace() with { Operation = "panel-image", RequestId = "image-1" },
            Trace() with { Operation = "panel-image", RequestId = "image-2", Status = "failed" }
        ];

        Assert.Equal(3, AiTraceFormatting.Select(traces).Count);
        Assert.Equal(["image-1", "image-2"], AiTraceFormatting.Select(traces, images: true).Select(trace => trace.RequestId));
        Assert.Single(AiTraceFormatting.Select(traces, images: false));
        Assert.Empty(AiTraceFormatting.Select(null));
    }

    [Fact]
    public void ScaffoldProvenanceSurvivesDraftTransferAndRequestSerialization()
    {
        AiGenerationTrace[] traces = [Trace() with { Operation = "scaffold" }];
        var scaffoldId = Guid.NewGuid();
        var scaffold = new ProjectScaffoldResponse("Title", "Description", "Context", ["Scene"], traces, scaffoldId);
        var projectId = Guid.NewGuid();
        var drafts = new StoryDraftState();
        drafts.Store(projectId, new PreparedComic(BoardTemplate.Standard, scaffold));
        var restored = drafts.Take(projectId)!;
        var request = new CreateProjectRequest("Title", null, ScaffoldId: restored.Scaffold.ScaffoldId);
        var serialized = JsonSerializer.Serialize(request);
        var roundTrip = JsonSerializer.Deserialize<CreateProjectRequest>(serialized)!;

        Assert.Equal(traces, restored.Scaffold.Traces);
        Assert.Equal(scaffoldId, roundTrip.ScaffoldId);
        Assert.DoesNotContain("ScaffoldTraces", serialized);
        Assert.Null(drafts.Take(projectId));
    }

    [Fact]
    public void BoardScaffoldRequestsTransferOnlyTheOpaqueId()
    {
        var scaffoldId = Guid.NewGuid();
        var request = new CreateBoardRequest("Title", null, true, BoardTemplate.Standard, [], scaffoldId);
        var serialized = JsonSerializer.Serialize(request);
        var roundTrip = JsonSerializer.Deserialize<CreateBoardRequest>(serialized)!;

        Assert.Equal(scaffoldId, roundTrip.ScaffoldId);
        Assert.DoesNotContain("ScaffoldTraces", serialized);
    }

    [Fact]
    public async Task RepeatedRequestsRenderIndividuallyWithAccessibleCollapsedDetails()
    {
        var trace = Trace() with { Provider = "<provider>", TotalTokens = 100, UsageSource = "provider-reported" };
        var html = await RenderAsync([trace, trace]);

        Assert.Equal(2, Regex.Matches(html, "role=\"tooltip\"").Count);
        Assert.Equal(2, Regex.Matches(html, "aria-expanded=\"false\"").Count);
        Assert.Equal(2, Regex.Matches(html, @"\shidden(?:=""[^""]*"")?(?=[\s>])").Count);
        Assert.Equal(2, Regex.Matches(html, "aria-describedby=\"ai-trace-").Count);
        Assert.Equal(2, Regex.Matches(html, "<button").Count);
        Assert.Equal(2, Regex.Matches(html, "<button type=\"button\"").Count);
        Assert.Equal(2, Regex.Matches(System.Net.WebUtility.HtmlDecode(html), "AI · MAI-Thinking").Count);
        Assert.Contains("&lt;provider&gt;", html);
        Assert.DoesNotContain("title=", html);
        foreach (Match description in Regex.Matches(html, "aria-describedby=\"([^\"]+)\""))
            Assert.Contains($"id=\"{description.Groups[1].Value}\"", html);
    }

    [Fact]
    public async Task IndicatorEncodesUnsafeModelInVisibleLabelDescriptionAndDetails()
    {
        var unsafeModel = "<img src=x onerror=alert(1)> & \"model\"";
        var html = await RenderComponentAsync<AiTraceIndicator>(new()
        {
            ["Trace"] = Trace() with { Model = unsafeModel }
        });

        Assert.Contains("AI · &lt;img", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain(unsafeModel, html);
        Assert.Contains("&amp;", html);
        Assert.Contains("&quot;model&quot;", html);
        Assert.Contains("type=\"button\"", html);
        Assert.Contains("aria-describedby=\"ai-trace-", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Contains("role=\"tooltip\"", html);
        Assert.Single(Regex.Matches(html, @"\shidden(?:=""[^""]*"")?(?=[\s>])").Cast<Match>());
    }

    [Fact]
    public async Task LegacyRecordsWithoutTracesDoNotRenderInventedMetadata()
    {
        Assert.Equal(string.Empty, await RenderAsync(null));
    }

    private static AiGenerationTrace Trace() => new()
    {
        Operation = "prompt-rewrite",
        Model = "MAI-Thinking",
        Provider = "Azure AI Foundry",
        Status = "completed"
    };

    private static Task<string> RenderAsync(IReadOnlyList<AiGenerationTrace>? traces) =>
        RenderComponentAsync<AiTraceMetadata>(new() { ["Traces"] = traces });

    private static async Task<string> RenderComponentAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return rendered.ToHtmlString();
        });
    }
}
