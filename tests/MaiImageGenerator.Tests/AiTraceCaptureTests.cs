using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;

namespace MaiImageGenerator.Tests;

public sealed class AiTraceCaptureTests
{
    [Fact]
    public void CaptureChatResponse_PreservesIndependentCountsAndDoesNotInferTotal()
    {
        var trace = new AiGenerationTrace();

        AiTraceCapture.CaptureChatResponse(trace, () => JsonDocument.Parse("""
            {"id":"response-123","usage":{"prompt_tokens":12,"completion_tokens":7}}
            """));

        Assert.Equal(12, trace.InputTokens);
        Assert.Equal(7, trace.OutputTokens);
        Assert.Null(trace.TotalTokens);
        Assert.Equal("provider-reported", trace.UsageSource);
        Assert.Equal("response-123", trace.RequestId);
    }

    [Fact]
    public void CaptureChatResponse_PreservesReportedTotalEvenWhenItDiffersFromSum()
    {
        var trace = new AiGenerationTrace();

        AiTraceCapture.CaptureChatResponse(trace, () => JsonDocument.Parse("""
            {"usage":{"prompt_tokens":12,"completion_tokens":7,"total_tokens":99}}
            """));

        Assert.Equal(99, trace.TotalTokens);
    }

    [Fact]
    public void CaptureChatResponse_AbsentUsageStaysNullAndEmitsSafeTelemetry()
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AiTraceCapture.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    lock (outcomes)
                    {
                        outcomes.Add((string)tag.Value!);
                    }
                }
            }
        });
        listener.Start();
        var trace = new AiGenerationTrace();

        AiTraceCapture.CaptureChatResponse(trace, () => JsonDocument.Parse("{}"));
        AiTraceCapture.CaptureChatResponse(trace, () => throw new InvalidOperationException("not retained"));

        Assert.Null(trace.InputTokens);
        Assert.Null(trace.OutputTokens);
        Assert.Null(trace.TotalTokens);
        Assert.Equal("unavailable", trace.UsageSource);
        lock (outcomes)
        {
            Assert.Contains("usage-unavailable", outcomes);
            Assert.Contains("capture-failed", outcomes);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"usage":null}""")]
    [InlineData("""{"usage":{"input_tokens":"bad","output_tokens":-1,"total_tokens":1.5}}""")]
    [InlineData("""{"usage":{"input_tokens":99999999999999999999999999999}}""")]
    [InlineData("not JSON")]
    public void CaptureImageResponse_MissingOrMalformedUsageNeverInventsCounts(string response)
    {
        var trace = new AiGenerationTrace();

        AiTraceCapture.CaptureImageResponse(trace, () => JsonDocument.Parse(response));

        Assert.Null(trace.InputTokens);
        Assert.Null(trace.OutputTokens);
        Assert.Null(trace.TotalTokens);
        Assert.Equal("unavailable", trace.UsageSource);
    }

    [Fact]
    public void CaptureImageResponse_AllowListsMetadataAndPreservesZero()
    {
        var trace = new AiGenerationTrace();

        AiTraceCapture.CaptureImageResponse(trace, () => JsonDocument.Parse("""
            {"data":[{"b64_json":"private image"}],"prompt":"private prompt","usage":
              {"input_tokens":"invalid","output_tokens":0,"total_tokens":31,"private":"not retained"}}
            """));
        var serialized = JsonSerializer.Serialize(trace);

        Assert.Null(trace.InputTokens);
        Assert.Equal(0, trace.OutputTokens);
        Assert.Equal(31, trace.TotalTokens);
        Assert.Equal(1, trace.ImageCount);
        Assert.Equal("provider-reported", trace.UsageSource);
        Assert.DoesNotContain("private", serialized);
    }

    [Fact]
    public void CaptureRequestId_UsesOnlySafeAllowListedHeaders()
    {
        var trace = new AiGenerationTrace();

        AiTraceCapture.CaptureRequestId(trace, name => name switch
        {
            "x-request-id" => "unsafe request id\nsecret",
            "apim-request-id" => "safe-request-id",
            _ => throw new InvalidOperationException("Unexpected header access")
        });

        Assert.Equal("safe-request-id", trace.RequestId);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("canceled")]
    public void CaptureScope_FinalizesEveryOutcome(string status)
    {
        var trace = new AiGenerationTrace();
        var before = DateTimeOffset.UtcNow;
        using (var capture = AiTraceCapture.Begin(trace, "panel-image", "configured-model", "1365x768"))
        {
            if (status == "completed")
            {
                capture.Complete();
            }
            else if (status == "canceled")
            {
                capture.Cancel();
            }
        }

        Assert.Equal(status, trace.Status);
        Assert.Equal("Microsoft Foundry", trace.Provider);
        Assert.Equal("configured-model", trace.Model);
        Assert.Equal("1365x768", trace.ImageSize);
        Assert.Null(trace.ImageQuality);
        Assert.InRange(trace.StartedAt, before, DateTimeOffset.UtcNow);
        Assert.NotNull(trace.CompletedAt);
        Assert.True(trace.CompletedAt >= trace.StartedAt);
        Assert.True(trace.DurationMs >= 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageGenerator_CapturesHttpAndSdkEditMetadata(bool edit)
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse("""
            {"created":1,"data":[{"b64_json":"AQID"}],
             "usage":{"input_tokens":15,"output_tokens":25,"total_tokens":73}}
            """))));
        var generator = CreateImageGenerator(httpClient);
        var trace = new AiGenerationTrace { Operation = "chapter-image" };

        var result = await generator.GenerateAsync("not retained", edit ? [1, 2, 3] : null,
            CancellationToken.None, trace);

        Assert.Equal(new byte[] { 1, 2, 3 }, result);
        Assert.Equal(15, trace.InputTokens);
        Assert.Equal(25, trace.OutputTokens);
        Assert.Equal(73, trace.TotalTokens);
        Assert.Equal("request-123", trace.RequestId);
        Assert.Equal("configured-image", trace.Model);
        Assert.Equal("chapter-image", trace.Operation);
        Assert.Equal("1024x1024", trace.ImageSize);
        Assert.Equal(1, trace.ImageCount);
        Assert.Equal("completed", trace.Status);
        Assert.NotNull(trace.CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageGenerator_UsageAbsenceDoesNotFailImageGeneration(bool edit)
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse("""
            {"data":[{"b64_json":"AQID"}],"usage":{"total_tokens":"malformed"}}
            """))));
        var trace = new AiGenerationTrace();

        await CreateImageGenerator(httpClient).GenerateAsync("not retained", edit ? [1] : null,
            CancellationToken.None, trace);

        Assert.Equal("completed", trace.Status);
        Assert.Equal("unavailable", trace.UsageSource);
        Assert.Null(trace.TotalTokens);
    }

    [Fact]
    public async Task ImageGenerator_FailureRetainsRequestIdAndFinalizesTrace()
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) => Task.FromResult(
            JsonResponse("""{"error":{"message":"not retained"}}""", HttpStatusCode.BadRequest))));
        var trace = new AiGenerationTrace();

        await Assert.ThrowsAsync<FoundryGenerationException>(() =>
            CreateImageGenerator(httpClient).GenerateAsync("not retained", null, CancellationToken.None, trace));

        Assert.Equal("failed", trace.Status);
        Assert.Equal("request-123", trace.RequestId);
        Assert.NotNull(trace.CompletedAt);
        Assert.Null(trace.TotalTokens);
    }

    [Fact]
    public async Task ImageGenerator_CancellationFinalizesTrace()
    {
        using var cancellation = new CancellationTokenSource();
        using var httpClient = new HttpClient(new FakeHandler((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));
        var trace = new AiGenerationTrace();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateImageGenerator(httpClient).GenerateAsync("not retained", null, cancellation.Token, trace));

        Assert.Equal("canceled", trace.Status);
        Assert.NotNull(trace.CompletedAt);
    }

    [Fact]
    public async Task ProjectScaffolder_CapturesTypedAgentUsageBeforeOutputParsing()
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) => Task.FromResult(
            ChatResponse("""{"title":"Title","description":"Description","context":"Style","panelPrompts":["Scene"]}"""))));
        var scaffolder = new ProjectScaffolder(CreateOpenAIClient(httpClient), FoundryOptions());

        var result = await scaffolder.CreateAsync(new CreateProjectScaffoldRequest("Story", 1), CancellationToken.None);

        var trace = Assert.Single(result.Traces!);
        Assert.Equal(11, trace.InputTokens);
        Assert.Equal(7, trace.OutputTokens);
        Assert.Equal(37, trace.TotalTokens);
        Assert.Equal("request-123", trace.RequestId);
        Assert.Equal("configured-prompt", trace.Model);
        Assert.Equal("completed", trace.Status);
        Assert.NotNull(trace.CompletedAt);
    }

    [Fact]
    public async Task ProjectScaffolder_InvalidContentRetainsUsageInFailedTrace()
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) =>
            Task.FromResult(ChatResponse("invalid scaffold"))));
        var scaffolder = new ProjectScaffolder(CreateOpenAIClient(httpClient), FoundryOptions());
        var trace = new AiGenerationTrace();

        await Assert.ThrowsAsync<FoundryGenerationException>(() =>
            scaffolder.CreateAsync(new CreateProjectScaffoldRequest("Story", 1), CancellationToken.None, trace));

        Assert.Equal(37, trace.TotalTokens);
        Assert.Equal("failed", trace.Status);
        Assert.NotNull(trace.CompletedAt);
    }

    [Fact]
    public async Task ProjectScaffolder_HttpFailureRetainsSafeRequestId()
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) => Task.FromResult(
            JsonResponse("""{"error":{"message":"not retained"}}""", HttpStatusCode.BadRequest))));
        var scaffolder = new ProjectScaffolder(CreateOpenAIClient(httpClient), FoundryOptions());
        var trace = new AiGenerationTrace();

        await Assert.ThrowsAsync<FoundryGenerationException>(() =>
            scaffolder.CreateAsync(new CreateProjectScaffoldRequest("Story", 1), CancellationToken.None, trace));

        Assert.Equal("failed", trace.Status);
        Assert.Equal("request-123", trace.RequestId);
        Assert.Null(trace.TotalTokens);
        Assert.NotNull(trace.CompletedAt);
    }

    [Fact]
    public async Task ProjectScaffolder_IgnoresModelSuppliedTraceMetadataAndScaffoldIdentity()
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) => Task.FromResult(ChatResponse("""
            {"title":"Title","description":"Description","context":"Style","panelPrompts":["Scene"],
             "scaffoldId":"untrusted-invalid-id",
             "traces":[{"operation":"untrusted","model":"untrusted","totalTokens":999}]}
            """))));
        var scaffolder = new ProjectScaffolder(CreateOpenAIClient(httpClient), FoundryOptions());

        var result = await scaffolder.CreateAsync(new CreateProjectScaffoldRequest("Story", 1), CancellationToken.None);

        Assert.Null(result.ScaffoldId);
        var trace = Assert.Single(result.Traces!);
        Assert.Equal("project-scaffold", trace.Operation);
        Assert.Equal("configured-prompt", trace.Model);
        Assert.Equal(37, trace.TotalTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectScaffolder_OnlyReportsZeroUsageWhenProviderExplicitlySuppliesIt(bool reportUsage)
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) => Task.FromResult(ChatResponse(
            """{"title":"Title","description":"Description","context":"Style","panelPrompts":["Scene"]}""",
            reportUsage, zeroUsage: true))));
        var scaffolder = new ProjectScaffolder(CreateOpenAIClient(httpClient), FoundryOptions());

        var result = await scaffolder.CreateAsync(new CreateProjectScaffoldRequest("Story", 1), CancellationToken.None);

        var trace = Assert.Single(result.Traces!);
        Assert.Equal(reportUsage ? 0L : null, trace.InputTokens);
        Assert.Equal(reportUsage ? 0L : null, trace.OutputTokens);
        Assert.Equal(reportUsage ? 0L : null, trace.TotalTokens);
        Assert.Equal(reportUsage ? "provider-reported" : "unavailable", trace.UsageSource);
    }

    [Theory]
    [InlineData("""{"prompt_tokens":11}""", 11L, null, null)]
    [InlineData("""{"completion_tokens":7}""", null, 7L, null)]
    [InlineData("""{"total_tokens":37}""", null, null, 37L)]
    [InlineData("""{"prompt_tokens":0}""", 0L, null, null)]
    [InlineData("""{"prompt_tokens":"malformed","completion_tokens":7}""", null, 7L, null)]
    [InlineData("""{"prompt_tokens":5000000000}""", 5000000000L, null, null)]
    [InlineData("""{"completion_tokens_details":{"reasoning_tokens":"malformed"}}""", null, null, null)]
    [InlineData("null", null, null, null)]
    [InlineData("\"malformed\"", null, null, null)]
    [InlineData("{}", null, null, null)]
    public async Task ProjectScaffolder_PartialUsageDoesNotInventMissingCounters(
        string usage, long? input, long? output, long? total)
    {
        using var httpClient = new HttpClient(new FakeHandler((_, _) => Task.FromResult(ChatResponse(
            """{"title":"Title","description":"Description","context":"Style","panelPrompts":["Scene"]}""",
            usageOverride: JsonSerializer.Deserialize<JsonElement>(usage)))));
        var scaffolder = new ProjectScaffolder(CreateOpenAIClient(httpClient), FoundryOptions());

        var result = await scaffolder.CreateAsync(new CreateProjectScaffoldRequest("Story", 1), CancellationToken.None);

        var trace = Assert.Single(result.Traces!);
        Assert.Equal(input, trace.InputTokens);
        Assert.Equal(output, trace.OutputTokens);
        Assert.Equal(total, trace.TotalTokens);
        Assert.Equal(input.HasValue || output.HasValue || total.HasValue ? "provider-reported" : "unavailable",
            trace.UsageSource);
    }

    [Fact]
    public async Task ProjectScaffolder_ConcurrentRequestsKeepUsageIsolated()
    {
        var arrived = 0;
        var bothRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new FakeHandler(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var requestDocument = JsonDocument.Parse(body);
            Assert.Equal("configured-prompt", requestDocument.RootElement.GetProperty("model").GetString());
            if (Interlocked.Increment(ref arrived) == 2)
            {
                bothRequests.SetResult();
            }

            await bothRequests.Task.WaitAsync(cancellationToken);
            var input = body.Contains("first story", StringComparison.Ordinal) ? 10 : 20;
            return ChatResponse(
                """{"title":"Title","description":"Description","context":"Style","panelPrompts":["Scene"]}""",
                usageOverride: new { prompt_tokens = input, completion_tokens = 1, total_tokens = input + 10 });
        }));
        var scaffolder = new ProjectScaffolder(CreateOpenAIClient(httpClient), FoundryOptions());

        var results = await Task.WhenAll(
            scaffolder.CreateAsync(new CreateProjectScaffoldRequest("first story", 1), CancellationToken.None),
            scaffolder.CreateAsync(new CreateProjectScaffoldRequest("second story", 1), CancellationToken.None));

        Assert.Equal(10, Assert.Single(results[0].Traces!).InputTokens);
        Assert.Equal(20, Assert.Single(results[0].Traces!).TotalTokens);
        Assert.Equal(20, Assert.Single(results[1].Traces!).InputTokens);
        Assert.Equal(30, Assert.Single(results[1].Traces!).TotalTokens);
    }

    [Fact]
    public async Task UpdatePlan_ClonesOwnedTraceValuesWhenReplacingAndReorderingTrackedChapters()
    {
        using var context = new MaiImageGeneratorDbContext(
            new DbContextOptionsBuilder<MaiImageGeneratorDbContext>()
                .UseCosmos("https://localhost:8081", Convert.ToBase64String(new byte[64]), "trace-tests")
                .AddInterceptors(new OfflineSaveInterceptor())
                .Options);
        var savedTrace = new AiGenerationTrace
        {
            Operation = "chapter-prose",
            Provider = "Microsoft Foundry",
            Model = "configured-prompt",
            RequestId = "safe-request-id",
            InputTokens = 11,
            OutputTokens = 7,
            TotalTokens = 37,
            UsageSource = "provider-reported",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 1000,
            ImageCount = 1,
            ImageSize = "1024x1024",
            ImageQuality = "high",
            Status = "completed"
        };
        var savedChapters = Enumerable.Range(1, 2).Select(number => new BookChapter
        {
            Id = $"chapter-{number}",
            Number = number,
            Title = $"Chapter {number}",
            Summary = "Summary",
            CharacterEvents = "Events",
            Continuity = "Continuity",
            ProseTraces = [AiTraceCapture.Clone(savedTrace)],
            ImageTraces = [AiTraceCapture.Clone(savedTrace)]
        }).ToArray();
        var project = new ProjectDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = "owner-a",
            Type = ProjectType.Book,
            BookPlan = new BookPlan { Chapters = savedChapters.ToList() }
        };
        context.Attach(project);
        var request = new UpdateBookPlanRequest(savedChapters.Reverse().Select((chapter, index) =>
            new BookChapterRequest(chapter.Id, index + 1, chapter.Title, chapter.Summary, chapter.CharacterEvents,
                chapter.Continuity, null, null)).ToArray());
        using var httpClient = new HttpClient();
        var service = new BookService(CreateOpenAIClient(httpClient), FoundryOptions(), context,
            CreateImageGenerator(httpClient),
            new BlobServiceClient(new Uri("https://example.test"), new FakeCredential()),
            new BookPlanGenerationQueue(
                new ServiceBusClient("example.servicebus.windows.net", new FakeCredential()).CreateSender("plans")),
            new ImageGenerationCoordinator(FoundryOptions().Value),
            NullLogger<BookService>.Instance);

        await service.UpdatePlanAsync(project, request, CancellationToken.None);
        context.ChangeTracker.DetectChanges();

        Assert.Equal(["chapter-2", "chapter-1"], project.BookPlan.Chapters.Select(chapter => chapter.Id));
        foreach (var chapter in project.BookPlan.Chapters)
        {
            var oldChapter = savedChapters.Single(saved => saved.Id == chapter.Id);
            Assert.NotSame(oldChapter, chapter);
            var proseTrace = Assert.Single(chapter.ProseTraces);
            var imageTrace = Assert.Single(chapter.ImageTraces);
            Assert.NotSame(Assert.Single(oldChapter.ProseTraces), proseTrace);
            Assert.NotSame(Assert.Single(oldChapter.ImageTraces), imageTrace);
            Assert.Equal(JsonSerializer.Serialize(savedTrace), JsonSerializer.Serialize(proseTrace));
            Assert.Equal(JsonSerializer.Serialize(savedTrace), JsonSerializer.Serialize(imageTrace));
        }
    }

    private static IOptions<FoundryOptions> FoundryOptions() => Options.Create(new FoundryOptions
    {
        Endpoint = "https://example.test",
        PromptModel = "configured-prompt",
        ImageModel = "configured-image"
    });

    private static OpenAIClient CreateOpenAIClient(HttpClient httpClient) =>
        new(new ApiKeyCredential("unused-test-value"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.test/openai/v1/"),
            Transport = new HttpClientPipelineTransport(httpClient)
        });

    private static FoundryImageGenerator CreateImageGenerator(HttpClient httpClient) =>
        new(CreateOpenAIClient(httpClient).GetImageClient("configured-image"),
            new FakeHttpClientFactory(httpClient), new FakeCredential(), FoundryOptions());

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "request-123");
        return response;
    }

    private static HttpResponseMessage ChatResponse(string content, bool reportUsage = true, bool zeroUsage = false,
        object? usageOverride = null)
    {
        var response = new Dictionary<string, object>
        {
            ["id"] = "chat-response-1",
            ["object"] = "chat.completion",
            ["created"] = 1,
            ["model"] = "configured-prompt",
            ["choices"] = new[]
            {
                new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" }
            }
        };
        if (reportUsage)
        {
            response["usage"] = usageOverride ?? new
            {
                prompt_tokens = zeroUsage ? 0 : 11,
                completion_tokens = zeroUsage ? 0 : 7,
                total_tokens = zeroUsage ? 0 : 37
            };
        }

        return JsonResponse(JsonSerializer.Serialize(response));
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("unused-test-value", DateTimeOffset.UtcNow.AddMinutes(5));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class OfflineSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
    }
}
