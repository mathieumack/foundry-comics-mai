using System.Reflection;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Api.Controllers;
using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MaiImageGenerator.Tests;

public sealed class PanelVersionApiTests
{
    [Fact]
    public async Task AcceptancePersistsBeforeQueue_AndSameCommandResumesAfterDispatchFailure()
    {
        var saved = new Checkpoints();
        await using var db = Context(saved);
        var (document, project) = Board();
        db.Attach(project);
        db.Attach(document);
        var queue = new Queue { Fail = true };
        var service = Service(db, queue);
        var id = Guid.NewGuid();
        Func<GenerationDocument, bool> apply = item =>
            PanelVersionCommands.CreateAttempt(item, 1, id, 0, true, null, null, DateTimeOffset.UtcNow);
        queue.BeforeSend = () =>
        {
            Assert.Equal(id.ToString("D"), saved.Saved.Last().PendingCommandId);
            Assert.True(BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow));
        };
        await Assert.ThrowsAsync<PanelCommandDispatchException>(() =>
            Execute(service, document, project, id, apply));
        Assert.Equal(GenerationStatus.Pending, document.Status);
        Assert.Equal(2, document.Panels[0].Versions.Count);
        Assert.False(BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow));
        queue.Fail = false;
        var response = await Execute(service, document, project, id, apply);
        var message = Assert.Single(queue.Messages);
        var contract = message.Body.ToObjectFromJson<BoardGenerationRequestMessage>()!;
        Assert.Equal(4, contract.SchemaVersion);
        Assert.Equal("panel", contract.Operation);
        Assert.Equal(1, contract.PanelNumber);
        Assert.Equal(document.Id, contract.RunId);
        Assert.Equal(document.OwnerId, contract.OwnerId);
        Assert.Equal(id.ToString("D"), contract.CommandId);
        Assert.Equal($"{document.Id}:{id:D}", message.MessageId);
        Assert.Equal(1, response.Revision);
        Assert.True(response.CanEditPanels);
        Assert.Equal(id.ToString("D"), response.PendingCommandId);
        Assert.Equal(2, document.Panels[0].Versions.Count);
        document.Status = GenerationStatus.Completed;
        document.PendingCommandId = null;
        await Execute(service, document, project, id, apply);
        Assert.Single(queue.Messages);
    }

    [Fact]
    public async Task OptimisticConflictDiscardsRejectedMutation_WithoutSavingItDuringLeaseRelease()
    {
        var saved = new Checkpoints { RejectCommand = true };
        await using var db = Context(saved);
        var (document, project) = Board();
        db.Attach(project);
        db.Attach(document);
        var id = Guid.NewGuid();
        var queue = new Queue();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            Execute(Service(db, queue), document, project, id, item =>
                PanelVersionCommands.CreateAttempt(item, 1, id, 0, true, null, null, DateTimeOffset.UtcNow)));
        Assert.Equal(1, saved.RejectedWrites);
        Assert.Empty(queue.Messages);
        Assert.Empty(db.ChangeTracker.Entries<GenerationDocument>());
        Assert.False(BoardSubmissionLease.IsActive(project, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task InvalidAndDeletingRequestsNeverDispatchOrSaveVersions()
    {
        var saved = new Checkpoints();
        await using var db = Context(saved);
        var (document, project) = Board();
        db.Attach(project);
        db.Attach(document);
        var queue = new Queue();
        var service = Service(db, queue);
        var id = Guid.NewGuid();
        document.Panels[0].Issue = PanelGenerationIssue.ContentSafetyRejected;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Execute(service, document, project, id, item =>
                PanelVersionCommands.CreateAttempt(item, 1, id, 0, true, null, null, DateTimeOffset.UtcNow)));
        Assert.All(saved.Saved, item => Assert.Empty(item.Panels[0].Versions));
        project.DeletionRequestedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<PanelCommandConflictException>(() =>
            Execute(service, document, project, id, _ => throw new InvalidOperationException()));
        project.DeletionRequestedAt = null;
        project.OwnerId = "other-owner";
        await Assert.ThrowsAsync<ArgumentException>(() => Execute(service, document, project, id, _ => true));
        Assert.Empty(queue.Messages);
    }

    [Fact]
    public void DetailExposesVersionHistoryWithoutPrivateArtifactPaths_AndPreservesCompleteness()
    {
        var (document, project) = Board();
        document.Panels[0].Status = PanelGenerationStatus.Completed;
        document.Panels[0].ImageBlobName = ArtifactPathBuilder.GetGenerationBlobName(
            document.OwnerId, Guid.Parse(document.Id), "panel-01.png");
        var id = Guid.NewGuid();
        PanelVersionCommands.CreateAttempt(document, 1, id, 0, false, "Revised scene", null, DateTimeOffset.UtcNow);
        document.Panels[0].Versions.Last().Issue = PanelGenerationIssue.ContentSafetyRejected;
        document.Panels[0].Versions.Last().Status = PanelGenerationStatus.Failed;
        var response = GenerationService.ToResponse(document, project);
        Assert.Equal(GenerationCompleteness.Complete, response.Completeness);
        Assert.Equal(1, response.CompletedPanelCount);
        Assert.Equal(0, response.FailedPanelCount);
        Assert.Equal(2, response.Panels[0].Versions!.Count);
        Assert.Equal(PanelVersionCommands.OriginalVersionId, response.Panels[0].SelectedVersionId);
        Assert.Equal($"api/generations/{document.Id}/panels/1/versions/{Guid.Empty:D}/image",
            response.Panels[0].ImageUri!.OriginalString);
        var json = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
        Assert.DoesNotContain(document.OwnerId, json);
        Assert.DoesNotContain(document.Panels[0].ImageBlobName!, json);
        Assert.DoesNotContain("imageBlobName", json);
    }

    [Fact]
    public void CosmosModelOwnsVersionHistoryAndUsesETagConcurrency()
    {
        using var db = Context(new Checkpoints());
        var generation = db.Model.FindEntityType(typeof(GenerationDocument))!;
        Assert.True(generation.FindProperty(nameof(GenerationDocument.ETag))!.IsConcurrencyToken);
        var panel = generation.FindNavigation(nameof(GenerationDocument.Panels))!.TargetEntityType;
        var version = panel.FindNavigation(nameof(GenerationPanel.Versions))!.TargetEntityType;
        Assert.True(version.IsOwned());
        Assert.True(version.FindNavigation(nameof(PanelVersion.Traces))!.TargetEntityType.IsOwned());
        Assert.True(generation.FindNavigation(nameof(GenerationDocument.SelectionCommands))!.TargetEntityType.IsOwned());
    }

    [Fact]
    public async Task PanelEndpointsRequireAuthenticationBeforeAccessingServices()
    {
        var controller = new GenerationsController(new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            null!, null!, null!);
        Assert.IsType<UnauthorizedResult>((await controller.CreatePanelAttempt(Guid.NewGuid(), 1,
            new(Guid.NewGuid(), 0, true), CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await controller.SelectPanelVersion(Guid.NewGuid(), 1,
            new(Guid.NewGuid(), Guid.NewGuid(), 0), CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>(await controller.DownloadPanelVersion(
            Guid.NewGuid(), 1, Guid.NewGuid(), CancellationToken.None));
        Assert.IsType<UnauthorizedResult>((await controller.ResumePanelCommand(
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Result);
    }

    private static Task<GenerationResponse> Execute(GenerationService service, GenerationDocument document,
        ProjectDocument project, Guid id, Func<GenerationDocument, bool> apply) =>
        (Task<GenerationResponse>)typeof(GenerationService).GetMethod("ExecutePanelCommandAsync",
            BindingFlags.NonPublic | BindingFlags.Instance, [typeof(GenerationDocument), typeof(ProjectDocument),
                typeof(Guid), typeof(Func<GenerationDocument, bool>), typeof(CancellationToken)])!
            .Invoke(service, [document, project, id, apply, CancellationToken.None])!;

    private static (GenerationDocument, ProjectDocument) Board()
    {
        var project = new ProjectDocument { Id = Guid.NewGuid().ToString("D"), OwnerId = "private-owner" };
        var document = new GenerationDocument
        {
            Id = Guid.NewGuid().ToString("D"), ProjectId = project.Id, OwnerId = project.OwnerId,
            Status = GenerationStatus.Completed, SchemaVersion = 4,
            InputSnapshot = new()
            {
                Panels = [new() { Number = 1, Prompt = "Frozen scene" }]
            },
            Panels = [new() { Number = 1, Prompt = "Mutable projection", Status = PanelGenerationStatus.Failed }]
        };
        return (document, project);
    }

    private static MaiImageGeneratorDbContext Context(Checkpoints checkpoints) => new(
        new DbContextOptionsBuilder<MaiImageGeneratorDbContext>()
            .UseCosmos("https://localhost:8081", Convert.ToBase64String(new byte[64]), "panel-api-tests")
            .AddInterceptors(checkpoints).Options);

    private static GenerationService Service(MaiImageGeneratorDbContext db, Queue queue)
    {
        var blobs = new BlobServiceClient(new Uri("https://example.test"));
        return new(db, new ReferenceService(db, blobs), blobs, queue,
            new ScaffoldTraceStore(blobs, NullLogger<ScaffoldTraceStore>.Instance),
            new GenerationSnapshotStore(blobs, Options.Create(new FoundryOptions())),
            NullLogger<GenerationService>.Instance);
    }

    private sealed class Checkpoints : SaveChangesInterceptor
    {
        public bool RejectCommand { get; init; }
        public int RejectedWrites { get; private set; }
        public List<GenerationDocument> Saved { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            eventData.Context!.ChangeTracker.DetectChanges();
            var document = eventData.Context.Set<GenerationDocument>().Local.SingleOrDefault();
            if (document is not null)
            {
                if (RejectCommand && document.Revision > 0)
                {
                    RejectedWrites++;
                    throw new DbUpdateConcurrencyException("A newer ETag won.");
                }
                Saved.Add(JsonSerializer.Deserialize<GenerationDocument>(JsonSerializer.Serialize(document))!);
            }
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
    }

    private sealed class Queue : ServiceBusSender
    {
        public bool Fail { get; set; }
        public Action? BeforeSend { get; set; }
        public List<ServiceBusMessage> Messages { get; } = [];
        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            BeforeSend?.Invoke();
            if (Fail) throw new InvalidOperationException("Queue unavailable");
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
