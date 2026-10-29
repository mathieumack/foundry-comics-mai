using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using WebGenerationResponse = MaiImageGenerator.Web.Models.GenerationResponse;
using WebBookPlanResponse = MaiImageGenerator.Web.Models.BookPlanResponse;

namespace MaiImageGenerator.Tests;

public sealed class AiTracePersistenceTests
{
    [Fact]
    public void CosmosModel_MapsEveryTraceCollectionAsOwnedJson()
    {
        using var context = new MaiImageGeneratorDbContext(
            new DbContextOptionsBuilder<MaiImageGeneratorDbContext>()
                .UseCosmos("https://localhost:8081", Convert.ToBase64String(new byte[64]), "trace-tests")
                .Options);
        var generation = context.Model.FindEntityType(typeof(GenerationDocument))!;
        var project = context.Model.FindEntityType(typeof(ProjectDocument))!;
        AssertTraceMapping(generation, nameof(GenerationDocument.ScaffoldTraces), "scaffoldTraces");
        AssertTraceMapping(generation.FindNavigation(nameof(GenerationDocument.Panels))!.TargetEntityType,
            nameof(GenerationPanel.Traces), "traces");
        AssertTraceMapping(project, nameof(ProjectDocument.ScaffoldTraces), "scaffoldTraces");
        var plan = project.FindNavigation(nameof(ProjectDocument.BookPlan))!.TargetEntityType;
        AssertTraceMapping(plan, nameof(BookPlan.Traces), "traces");
        var chapter = plan.FindNavigation(nameof(BookPlan.Chapters))!.TargetEntityType;
        AssertTraceMapping(chapter, nameof(BookChapter.ProseTraces), "proseTraces");
        AssertTraceMapping(chapter, nameof(BookChapter.ImageTraces), "imageTraces");
    }

    [Fact]
    public void GenerationProjection_SerializesUsageIntoWebContractWithoutOwnership()
    {
        var trace = CreateTrace();
        var document = new GenerationDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = "private-owner",
            ScaffoldTraces = [CreateTrace("story-scaffold")],
            Panels = [new GenerationPanel { Number = 1, Traces = [trace] }]
        };
        var response = GenerationService.ToResponse(document, new ProjectDocument());
        var json = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
        var webResponse = JsonSerializer.Deserialize<WebGenerationResponse>(json, JsonSerializerOptions.Web)!;
        var mapped = Assert.Single(Assert.Single(webResponse.Panels).Traces!);
        Assert.Equal(17, mapped.InputTokens);
        Assert.Equal(23, mapped.OutputTokens);
        Assert.Equal(40, mapped.TotalTokens);
        Assert.Equal(trace.RequestId, mapped.RequestId);
        Assert.Single(webResponse.ScaffoldTraces!);
        Assert.DoesNotContain("private-owner", json);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyDocuments_AndMissingUsage_DoNotInventZeroCounters()
    {
        var document = JsonSerializer.Deserialize<GenerationDocument>(
            """{"schemaVersion":2,"panels":[{"number":1}]}""", JsonSerializerOptions.Web)!;
        Assert.Empty(document.ScaffoldTraces);
        Assert.Empty(Assert.Single(document.Panels).Traces);
        var project = JsonSerializer.Deserialize<ProjectDocument>(
            """{"schemaVersion":2,"bookPlan":{"chapters":[{"number":1}]}}""", JsonSerializerOptions.Web)!;
        Assert.Empty(project.BookPlan!.Traces);
        Assert.Empty(Assert.Single(project.BookPlan.Chapters).ImageTraces);
        Assert.Empty(Assert.Single(project.BookPlan.Chapters).ProseTraces);
        var trace = JsonSerializer.Deserialize<AiGenerationTrace>(
            JsonSerializer.Serialize(new AiGenerationTrace(), JsonSerializerOptions.Web),
            JsonSerializerOptions.Web)!;
        Assert.Null(trace.InputTokens);
        Assert.Null(trace.OutputTokens);
        Assert.Null(trace.TotalTokens);
        Assert.Equal("unavailable", trace.UsageSource);
    }

    [Fact]
    public void BookDocuments_RoundTripEachRequestSeparately()
    {
        var project = new ProjectDocument
        {
            BookPlan = new BookPlan
            {
                Traces = [CreateTrace("book-plan")],
                Chapters =
                [
                    new BookChapter
                    {
                        ProseTraces = [CreateTrace("chapter-prose"), CreateTrace("chapter-prose")],
                        ImageTraces = [new AiGenerationTrace { Operation = "image", ImageCount = 1, ImageSize = "1024x1024" }]
                    }
                ]
            }
        };
        var restored = JsonSerializer.Deserialize<ProjectDocument>(
            JsonSerializer.Serialize(project, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        Assert.Single(restored.BookPlan!.Traces);
        var chapter = Assert.Single(restored.BookPlan.Chapters);
        Assert.Equal(2, chapter.ProseTraces.Count);
        Assert.Null(Assert.Single(chapter.ImageTraces).TotalTokens);
    }

    [Fact]
    public void BookProjection_ExposesSeparatePlanProseAndImageUsage()
    {
        var project = new ProjectDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = ProjectType.Book,
            BookPlan = new BookPlan
            {
                Traces = [CreateTrace("book-plan")],
                Chapters =
                [
                    new BookChapter
                    {
                        Id = "chapter-1",
                        Number = 1,
                        ProseTraces = [CreateTrace("chapter-prose")],
                        ImageTraces = [new AiGenerationTrace { Operation = "chapter-image", ImageCount = 1 }]
                    }
                ]
            }
        };
        var json = JsonSerializer.Serialize(BookService.ToResponse(project), JsonSerializerOptions.Web);
        var response = JsonSerializer.Deserialize<WebBookPlanResponse>(json, JsonSerializerOptions.Web)!;
        Assert.Equal("book-plan", Assert.Single(response.Traces!).Operation);
        var chapter = Assert.Single(response.Chapters);
        Assert.Equal(17, Assert.Single(chapter.ProseTraces!).InputTokens);
        var image = Assert.Single(chapter.ImageTraces!);
        Assert.Equal("unavailable", image.UsageSource);
        Assert.Null(image.TotalTokens);
    }

    [Fact]
    public async Task ScaffoldTraceStore_PersistsServerTrace_AndScopesReadsToOwner()
    {
        var blobs = new MemoryBlobService();
        var store = new ScaffoldTraceStore(blobs, NullLogger<ScaffoldTraceStore>.Instance);
        var scaffold = await store.SaveAsync("owner-a",
            new ProjectScaffoldResponse("Title", "Description", "Context", ["Scene"], [CreateTrace("story-scaffold")]),
            CancellationToken.None);
        Assert.NotNull(scaffold.ScaffoldId);
        var trace = Assert.Single(await store.ReadAsync("owner-a", scaffold.ScaffoldId, CancellationToken.None));
        Assert.Equal(40, trace.TotalTokens);
        Assert.DoesNotContain("owner-a", Assert.Single(blobs.Content).Key);
        Assert.EndsWith($"/{scaffold.ScaffoldId:N}/scaffold-trace.json", Assert.Single(blobs.Content).Key);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ReadAsync("owner-b", scaffold.ScaffoldId, CancellationToken.None));
        Assert.Empty(await store.ReadAsync("owner-a", null, CancellationToken.None));
    }

    [Fact]
    public async Task ScaffoldTraceStore_MetadataStorageFailure_DoesNotDiscardGeneratedText()
    {
        var store = new ScaffoldTraceStore(new MemoryBlobService { FailUploads = true },
            NullLogger<ScaffoldTraceStore>.Instance);
        var original = new ProjectScaffoldResponse("Title", "Description", "Context", ["Scene"], [CreateTrace()]);
        var result = await store.SaveAsync("owner-a", original, CancellationToken.None);
        Assert.Same(original, result);
        Assert.Null(result.ScaffoldId);
        Assert.Single(result.Traces!);
    }

    private static AiGenerationTrace CreateTrace(string operation = "image") => new()
    {
        Provider = "Microsoft Foundry",
        Model = "test-model",
        Operation = operation,
        RequestId = "safe-request-id",
        StartedAt = DateTimeOffset.Parse("2026-10-29T19:00:00Z"),
        CompletedAt = DateTimeOffset.Parse("2026-10-29T19:00:01Z"),
        DurationMs = 1000,
        InputTokens = 17,
        OutputTokens = 23,
        TotalTokens = 40,
        UsageSource = "provider-reported",
        Status = "completed"
    };

    private static void AssertTraceMapping(IEntityType owner, string navigation, string jsonName)
    {
        var trace = owner.FindNavigation(navigation)!.TargetEntityType;
        Assert.True(trace.IsOwned());
        Assert.Equal(jsonName, trace.GetContainingPropertyName());
        Assert.Equal("inputTokens", trace.FindProperty(nameof(AiGenerationTrace.InputTokens))!.GetJsonPropertyName());
        Assert.Equal("outputTokens", trace.FindProperty(nameof(AiGenerationTrace.OutputTokens))!.GetJsonPropertyName());
        Assert.Equal("totalTokens", trace.FindProperty(nameof(AiGenerationTrace.TotalTokens))!.GetJsonPropertyName());
        Assert.True(trace.FindProperty(nameof(AiGenerationTrace.TotalTokens))!.IsNullable);
    }

    private sealed class MemoryBlobService : BlobServiceClient
    {
        public Dictionary<string, BinaryData> Content { get; } = [];
        public bool FailUploads { get; init; }
        public override BlobContainerClient GetBlobContainerClient(string blobContainerName) => new MemoryContainer(this);

        private sealed class MemoryContainer(MemoryBlobService service) : BlobContainerClient
        {
            public override BlobClient GetBlobClient(string blobName) => new MemoryBlob(service, blobName);
        }

        private sealed class MemoryBlob(MemoryBlobService service, string name) : BlobClient
        {
            public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content, CancellationToken cancellationToken)
            {
                if (service.FailUploads)
                {
                    throw new RequestFailedException(503, "Storage unavailable");
                }

                service.Content.Add(name, content);
                return Task.FromResult(Response.FromValue(
                    BlobsModelFactory.BlobContentInfo(new ETag("test"), DateTimeOffset.UtcNow, [], null!, null!, 0),
                    null!));
            }

            public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken) =>
                service.Content.TryGetValue(name, out var content)
                    ? Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadResult(content), null!))
                    : throw new RequestFailedException(404, "Not found");
        }
    }
}
