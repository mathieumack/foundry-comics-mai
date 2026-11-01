using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Worker = MaiImageGenerator.Jobs.BoardGeneration.Services;

namespace MaiImageGenerator.Tests;

public sealed class GenerationSnapshotTests
{
    private const string Owner = "owner-a";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly FoundryOptions Settings = new()
    {
        Endpoint = "https://example.test",
        PromptModel = "accepted-text",
        ImageModel = "accepted-image",
        ImageRetryCount = 1,
        ImageRetryBaseDelayMilliseconds = 1
    };

    [Fact]
    public async Task Submission_PersistsSnapshotBeforeQueue_AndReadsFrozenCharacters()
    {
        var checkpoints = new Checkpoints();
        await using var db = CreateContext(checkpoints);
        var blobs = new MemoryBlobs();
        var queue = new Queue(checkpoints);
        var project = CreateProject();
        db.Projects.Attach(project);
        queue.BeforeSend = _ => Assert.True(BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow));
        var service = CreateApi(db, blobs, queue);

        var response = await service.CreateBoardAsync(Owner, project,
            new CreateBoardRequest("Accepted title", null, false, null,
                [new PanelRequest("Accepted scene", ["hero"])]), CancellationToken.None);
        var document = Assert.Single(db.Generations.Local);
        Assert.Equal(4, document.SchemaVersion);
        Assert.False(BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow));
        Assert.Equal(document.Id, response.Run!.RunId);
        Assert.Equal("accepted-image", response.Run.Settings.ImageDeployment);
        Assert.Null(response.Run.Settings.ImageModelVersion);
        Assert.Equal(BoardInputVersions.PromptTemplate, response.Run.PromptTemplateVersion);
        var message = Assert.Single(queue.Messages).Body.ToObjectFromJson<BoardGenerationRequestMessage>()!;
        Assert.Equal(3, message.SchemaVersion);
        Assert.Equal(document.Id, message.RunId);
        Assert.NotNull(checkpoints.Saved.Last().InputSnapshot);

        project.Context = "Changed context";
        project.Characters[0].Name = "Changed identity";
        project.Characters.Clear();
        document.Context = "Changed projection";
        document.Panels[0].Prompt = "Changed projection";
        document.Panels[0].CharacterIds.Clear();
        var saved = GenerationService.ToResponse(document, project);
        Assert.Equal("Accepted identity", Assert.Single(saved.Characters).Name);
        Assert.Equal("Accepted context", saved.Context);
        Assert.Equal("Accepted scene", Assert.Single(saved.Panels).Prompt);
        Assert.Equal(["hero"], saved.Panels[0].CharacterIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submission_QueueFailureNeverReportsSuccess_AndKeepsCleanupAnchor(bool freeForm)
    {
        var checkpoints = new Checkpoints();
        await using var db = CreateContext(checkpoints);
        var service = CreateApi(db, new MemoryBlobs(), new Queue(checkpoints) { Fail = true });
        if (freeForm)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(Owner,
                new CreateGenerationRequest("Title", null, "Context", false, null,
                    [new PanelRequest("Scene", [])], []), CancellationToken.None));
            Assert.Single(db.Projects.Local);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBoardAsync(Owner, CreateProject(),
                new CreateBoardRequest("Title", null, false, null,
                    [new PanelRequest("Scene", [])]), CancellationToken.None));
        }
        var document = checkpoints.Saved.Last();
        Assert.Equal(GenerationStatus.Failed, document.Status);
        Assert.NotNull(document.InputSnapshot);
        Assert.Equal("The generation could not be queued.", document.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_UsesRetainedBytesAndFrozenMetadata_AfterReplacementOrDeletion(bool delete)
    {
        var blobs = new MemoryBlobs();
        var project = CreateProject();
        var document = CreateDocument(project);
        var reference = AddReference(blobs, project);
        document.InputSnapshot = await new GenerationSnapshotStore(blobs, Options.Create(Settings))
            .CaptureAsync(document, project, new Dictionary<string, ReferenceDocument> { [reference.Id] = reference },
                CancellationToken.None);
        var snapshot = document.InputSnapshot;
        var retained = Assert.Single(Assert.Single(snapshot.Characters).References);
        var acceptedBytes = blobs.Content["generations/" + retained.BlobName].ToArray();
        Assert.Equal(blobs.Content["references/" + reference.BlobName].ToArray(), acceptedBytes);
        project.Context = "Changed context";
        project.Characters[0].Name = "Changed identity";
        project.Characters[0].References.Clear();
        if (delete)
        {
            project.Characters.Clear();
            blobs.Content.Remove("references/" + reference.BlobName);
        }
        else
        {
            blobs.Content["references/" + reference.BlobName] = BinaryData.FromBytes([4, 5, 6]);
        }
        reference.BlobName = "changed-metadata";
        document.Panels[0].Prompt = "Changed scene";
        document.Panels[0].CharacterIds.Clear();
        document.Context = "Changed context";
        document.RewritePrompt = true;
        document.InputSnapshot = JsonSerializer.Deserialize<GenerationInputSnapshot>(
            JsonSerializer.Serialize(snapshot, Json), Json);

        var checkpoints = new Checkpoints();
        await using var db = CreateContext(checkpoints);
        db.Generations.Add(document);
        var provider = new Provider();
        var worker = CreateWorker(db, blobs, provider);
        var message = Message(document);
        // A queue field cannot override the accepted rewrite decision.
        await ProcessAsync(worker, message with { RewritePrompt = true }, document, project);

        Assert.Equal(GenerationStatus.Completed, document.Status);
        var request = Assert.Single(provider.Requests);
        Assert.Contains("accepted-image", request);
        Assert.Contains("Accepted identity", request);
        Assert.Contains("Accepted scene", request);
        Assert.DoesNotContain("Changed", request);
        Assert.Equal(acceptedBytes, blobs.Content["generations/" + retained.BlobName].ToArray());
        var response = GenerationService.ToResponse(document, project);
        Assert.Equal($"api/generations/{document.Id}/references/{reference.Id}/content",
            Assert.Single(Assert.Single(response.Characters).References).ContentUri!.OriginalString);
        Assert.DoesNotContain(retained.BlobName, JsonSerializer.Serialize(response, Json));
        var attempt = Assert.Single(document.Attempts);
        Assert.True(Guid.TryParse(attempt.Id, out _));
        Assert.Equal("completed", attempt.Status);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal(attempt.Id, Assert.Single(document.Panels[0].Traces).AttemptId);
        Assert.Contains(checkpoints.Saved, item => item.Attempts.Any(saved => saved.Id == attempt.Id));

        await ProcessAsync(worker, message, document, project);
        Assert.Single(document.Attempts);
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("foreign-path")]
    [InlineData("corrupt")]
    public async Task Worker_InvalidSnapshotFailsClosed_WithoutLiveReferenceFallback(string failure)
    {
        var blobs = new MemoryBlobs();
        var project = CreateProject();
        var document = CreateDocument(project);
        var reference = AddReference(blobs, project);
        var snapshot = await new GenerationSnapshotStore(blobs, Options.Create(Settings))
            .CaptureAsync(document, project, new Dictionary<string, ReferenceDocument> { [reference.Id] = reference },
                CancellationToken.None);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(snapshot, Json))!;
        var retained = snapshot.Characters[0].References[0];
        if (failure == "version") node["version"] = 99;
        if (failure == "foreign-path")
            node["characters"]![0]!["references"]![0]!["blobName"] =
                ArtifactPathBuilder.GetGenerationBlobName("owner-b", Guid.Parse(document.Id), "input");
        if (failure == "corrupt")
            blobs.Content["generations/" + retained.BlobName] = BinaryData.FromBytes([7]);
        document.InputSnapshot = failure == "missing" ? null :
            JsonSerializer.Deserialize<GenerationInputSnapshot>(node.ToJsonString(), Json);
        await using var db = CreateContext(new Checkpoints());
        var provider = new Provider();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            ProcessAsync(CreateWorker(db, blobs, provider), Message(document), document, project));

        Assert.Equal(GenerationStatus.Failed, document.Status);
        Assert.Equal("failed", Assert.Single(document.Attempts).Status);
        Assert.Empty(provider.Requests);
        Assert.Null(document.BoardBlobName);
    }

    [Fact]
    public async Task Worker_RedeliveryRetainsRunAndPreparedPrompt_ButRecordsSeparateAttemptsAndRetryTraces()
    {
        var project = CreateProject();
        var document = CreateDocument(project);
        var blobs = new MemoryBlobs();
        document.InputSnapshot = await new GenerationSnapshotStore(blobs, Options.Create(Settings))
            .CaptureAsync(document, project, new Dictionary<string, ReferenceDocument>(), CancellationToken.None);
        document.Panels[0].EffectivePrompt = "Previously prepared approved prompt";
        await using var db = CreateContext(new Checkpoints());
        var provider = new Provider { FailuresRemaining = 1 };
        var worker = CreateWorker(db, blobs, provider);
        await ProcessAsync(worker, Message(document), document, project);
        Assert.Equal(2, document.Panels[0].Traces.Count);
        Assert.All(document.Panels[0].Traces, trace => Assert.Equal(document.Attempts[0].Id, trace.AttemptId));
        Assert.All(provider.Requests, request => Assert.Contains("Previously prepared approved prompt", request));
        document.Status = GenerationStatus.Pending;
        blobs.Content.Remove("generations/" + document.Panels[0].ImageBlobName);
        await ProcessAsync(worker, Message(document), document, project);
        Assert.Equal(2, document.Attempts.Count);
        Assert.NotEqual(document.Attempts[0].Id, document.Attempts[1].Id);
        Assert.Equal(document.Id, document.InputSnapshot.RunId);
        Assert.Equal(document.Attempts[1].Id, document.Panels[0].Traces.Last().AttemptId);
        Assert.Equal(GenerationStatus.Completed, document.Status);
    }

    [Fact]
    public async Task LegacyRecords_HaveNoInventedSnapshotFacts_AndSchemaTwoStillExecutes()
    {
        var project = CreateProject();
        var document = JsonSerializer.Deserialize<GenerationDocument>(
            $$"""{"id":"{{Guid.NewGuid():D}}","ownerId":"{{Owner}}","projectId":"{{project.Id}}","schemaVersion":3,"panels":[{"number":1,"prompt":"Legacy scene"}]}""",
            Json)!;
        Assert.Null(document.InputSnapshot);
        Assert.Empty(document.Attempts);
        Assert.Null(GenerationService.ToResponse(document, project).Run);
        await using var db = CreateContext(new Checkpoints());
        var provider = new Provider();
        await ProcessAsync(CreateWorker(db, new MemoryBlobs(), provider), Message(document) with
        {
            SchemaVersion = 2,
            RunId = null,
            RewritePrompt = false
        }, document, project);
        Assert.Equal(GenerationStatus.Completed, document.Status);
        Assert.Null(document.InputSnapshot);
        Assert.Null(GenerationService.ToResponse(document, project).Run);
    }

    [Fact]
    public async Task RetentionAndDeletion_AreOwnerScoped_AndIncludeDetachedSnapshotArtifacts()
    {
        var blobs = new MemoryBlobs();
        var project = CreateProject();
        var document = CreateDocument(project);
        var reference = AddReference(blobs, project);
        var store = new GenerationSnapshotStore(blobs, Options.Create(Settings));
        reference.OwnerId = "owner-b";
        await Assert.ThrowsAsync<ArgumentException>(() => store.CaptureAsync(document, project,
            new Dictionary<string, ReferenceDocument> { [reference.Id] = reference }, CancellationToken.None));
        Assert.DoesNotContain(blobs.Content.Keys, key => key.StartsWith("generations/", StringComparison.Ordinal));
        reference.OwnerId = Owner;
        document.InputSnapshot = await store.CaptureAsync(document, project,
            new Dictionary<string, ReferenceDocument> { [reference.Id] = reference }, CancellationToken.None);
        var retained = document.InputSnapshot.Characters[0].References[0];
        await using var content = await store.OpenReferenceAsync(Owner, Guid.Parse(document.Id), retained, CancellationToken.None);
        Assert.Equal(Png(), BinaryData.FromStream(content).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.OpenReferenceAsync("owner-b", Guid.Parse(document.Id), retained, CancellationToken.None));
        var otherPath = ArtifactPathBuilder.GetGenerationBlobName("owner-b", Guid.Parse(document.Id), "input-retained");
        blobs.Content["generations/" + otherPath] = BinaryData.FromBytes([9]);
        project.Characters.Clear();
        await new BlobProjectArtifactStore(blobs).DeleteAsync(Owner, Guid.Parse(project.Id),
            [Guid.Parse(document.Id)], [reference.Id], CancellationToken.None);
        Assert.Equal("generations/" + otherPath, Assert.Single(blobs.Content).Key);
    }

    [Fact]
    public async Task SnapshotStorageFailure_DoesNotProduceAnAcceptedSnapshot()
    {
        var blobs = new MemoryBlobs { FailInputs = true };
        var project = CreateProject();
        var reference = AddReference(blobs, project);
        var document = CreateDocument(project);
        await Assert.ThrowsAsync<RequestFailedException>(() =>
            new GenerationSnapshotStore(blobs, Options.Create(Settings)).CaptureAsync(document, project,
                new Dictionary<string, ReferenceDocument> { [reference.Id] = reference }, CancellationToken.None));
        Assert.Null(document.InputSnapshot);
    }

    [Fact]
    public async Task Capture_IsProtectedFromDeletionThroughoutRetainedReferenceUpload()
    {
        var blobs = new MemoryBlobs();
        var project = CreateProject();
        var document = CreateDocument(project);
        var reference = AddReference(blobs, project);
        var lease = BoardSubmissionLease.Acquire(project, DateTimeOffset.UtcNow);
        blobs.BeforeInputUpload = () =>
        {
            Assert.True(BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow));
            Assert.Throws<ArgumentException>(() => BoardSubmissionLease.Acquire(project, DateTimeOffset.UtcNow));
        };
        document.InputSnapshot = await new GenerationSnapshotStore(blobs, Options.Create(Settings))
            .CaptureAsync(document, project, new Dictionary<string, ReferenceDocument> { [reference.Id] = reference },
                CancellationToken.None);
        BoardSubmissionLease.Release(project, lease);
        Assert.NotNull(document.InputSnapshot);
        Assert.False(BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow));
    }

    private static ProjectDocument CreateProject() => new()
    {
        Id = Guid.NewGuid().ToString("D"), OwnerId = Owner, Context = "Accepted context",
        Characters = [new ProjectCharacter { Id = "hero", Name = "Accepted identity" }]
    };

    private static GenerationDocument CreateDocument(ProjectDocument project) => new()
    {
        Id = Guid.NewGuid().ToString("D"), OwnerId = Owner, ProjectId = project.Id, SchemaVersion = 4,
        Title = "Accepted title", Context = project.Context, RewritePrompt = false,
        CreatedAt = DateTimeOffset.UtcNow,
        Panels = [new GenerationPanel { Number = 1, Prompt = "Accepted scene", CharacterIds = ["hero"] }]
    };

    private static ReferenceDocument AddReference(MemoryBlobs blobs, ProjectDocument project)
    {
        var id = Guid.NewGuid().ToString("N");
        var reference = new ReferenceDocument
        {
            Id = id, OwnerId = Owner, FileName = "identity.png", ContentType = "image/png",
            BlobName = ArtifactPathBuilder.GetReferenceBlobName(Owner, id, ".png")
        };
        project.Characters[0].References.Add(new ProjectCharacterReference { Id = id, FileName = reference.FileName });
        blobs.Content["references/" + reference.BlobName] = BinaryData.FromBytes(Png());
        return reference;
    }

    private static MaiImageGeneratorDbContext CreateContext(Checkpoints checkpoints) => new(
        new DbContextOptionsBuilder<MaiImageGeneratorDbContext>()
            .UseCosmos("https://localhost:8081", Convert.ToBase64String(new byte[64]), "snapshot-tests")
            .AddInterceptors(checkpoints).Options);

    private static GenerationService CreateApi(MaiImageGeneratorDbContext db, MemoryBlobs blobs, Queue queue) =>
        new(db, new ReferenceService(db, blobs), blobs, queue,
            new ScaffoldTraceStore(blobs, NullLogger<ScaffoldTraceStore>.Instance),
            new GenerationSnapshotStore(blobs, Options.Create(Settings)), NullLogger<GenerationService>.Instance);

    private static Worker.GenerationService CreateWorker(MaiImageGeneratorDbContext db, MemoryBlobs blobs, Provider provider)
    {
        var http = new HttpClient(provider);
        var openAI = new OpenAIClient(new ApiKeyCredential("unused-test-value"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.test/openai/v1/"),
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        });
        var changed = new FoundryOptions { Endpoint = "https://example.test", ImageModel = "changed-image" };
        var client = new Worker.FoundryImageClient(openAI, openAI.GetImageClient("changed-image"),
            http, new Credential(), Options.Create(changed));
        return new(db, new Worker.ReferenceService(db, blobs), blobs, client,
            new Worker.ImageComposer(Options.Create(changed)), new Worker.PanelGenerationNotifier(new Queue(), new Queue()),
            new ImageGenerationCoordinator(changed), NullLogger<Worker.GenerationService>.Instance);
    }

    private static BoardGenerationRequestMessage Message(GenerationDocument document) => new()
    {
        SchemaVersion = 3, OwnerId = document.OwnerId, ProjectId = document.ProjectId,
        BoardId = document.Id, RunId = document.Id
    };

    private static Task ProcessAsync(Worker.GenerationService worker, BoardGenerationRequestMessage message,
        GenerationDocument document, ProjectDocument project) =>
        (Task)typeof(Worker.GenerationService).GetMethod("ProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, [message, document, project, CancellationToken.None])!;

    private static byte[] Png()
    {
        using var image = new Image<Rgba32>(2, 2);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private sealed class Checkpoints : SaveChangesInterceptor
    {
        public List<GenerationDocument> Saved { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            eventData.Context!.ChangeTracker.DetectChanges();
            Saved.AddRange(eventData.Context.Set<GenerationDocument>().Local.Select(document =>
                JsonSerializer.Deserialize<GenerationDocument>(JsonSerializer.Serialize(document, Json), Json)!));
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
    }

    private sealed class Queue(Checkpoints? checkpoints = null) : ServiceBusSender
    {
        public bool Fail { get; init; }
        public Action<ServiceBusMessage>? BeforeSend { get; set; }
        public List<ServiceBusMessage> Messages { get; } = [];
        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            BeforeSend?.Invoke(message);
            if (checkpoints is not null) Assert.NotNull(checkpoints.Saved.Last().InputSnapshot);
            if (Fail) throw new InvalidOperationException("Queue unavailable");
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class Provider : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int FailuresRemaining { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(token));
            var fail = FailuresRemaining-- > 0;
            return new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(fail ? """{"error":{"code":"unavailable"}}""" :
                    $$"""{"data":[{"b64_json":"{{Convert.ToBase64String(Png())}}"}]}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) =>
            new("unused-test-value", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) =>
            ValueTask.FromResult(GetToken(context, token));
    }

    private sealed class MemoryBlobs : BlobServiceClient
    {
        public Dictionary<string, BinaryData> Content { get; } = [];
        public bool FailInputs { get; init; }
        public Action? BeforeInputUpload { get; set; }
        public override BlobContainerClient GetBlobContainerClient(string name) => new Container(this, name);

        private sealed class Container(MemoryBlobs service, string container) : BlobContainerClient
        {
            public override BlobClient GetBlobClient(string name) => new Blob(service, container + "/" + name);
            public override AsyncPageable<BlobItem> GetBlobsAsync(BlobTraits traits, BlobStates states,
                string? prefix = null, CancellationToken cancellationToken = default)
            {
                var items = states != BlobStates.None ? [] : service.Content.Keys
                    .Where(key => key.StartsWith(container + "/" + prefix, StringComparison.Ordinal))
                    .Select(key => BlobsModelFactory.BlobItem(name: key[(container.Length + 1)..])).ToArray();
                return AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(items, null, null!)]);
            }
        }

        private sealed class Blob(MemoryBlobs service, string key) : BlobClient
        {
            public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content,
                CancellationToken cancellationToken = default)
            {
                service.BeforeInputUpload?.Invoke();
                if (service.FailInputs) throw new RequestFailedException(503, "Storage unavailable");
                service.Content.Add(key, content);
                return Task.FromResult(Response.FromValue(
                    BlobsModelFactory.BlobContentInfo(new ETag("test"), DateTimeOffset.UtcNow, [], null!, null!, 0), null!));
            }
            public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content, bool overwrite,
                CancellationToken cancellationToken = default)
            {
                service.Content[key] = content;
                return Task.FromResult(Response.FromValue(
                    BlobsModelFactory.BlobContentInfo(new ETag("test"), DateTimeOffset.UtcNow, [], null!, null!, 0), null!));
            }
            public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken = default) =>
                service.Content.TryGetValue(key, out var content)
                    ? Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadResult(content), null!))
                    : throw new RequestFailedException(404, "Not found");
            public override Task<Response<BlobInfo>> SetHttpHeadersAsync(BlobHttpHeaders headers,
                BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(Response.FromValue(BlobsModelFactory.BlobInfo(new ETag("test"), DateTimeOffset.UtcNow), null!));
            public override Task<Response<bool>> DeleteIfExistsAsync(DeleteSnapshotsOption snapshotsOption = default,
                BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(Response.FromValue(service.Content.Remove(key), null!));
        }
    }
}
