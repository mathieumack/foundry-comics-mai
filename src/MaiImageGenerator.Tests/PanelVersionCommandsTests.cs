using System.Text.Json;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;

namespace MaiImageGenerator.Tests;

public sealed class PanelVersionCommandsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void RetryFreezesFailedInputs_AndNeverChangesOtherPanels()
    {
        var document = Board();
        var siblings = JsonSerializer.Serialize(document.Panels.Skip(1));
        var id = Guid.NewGuid();
        Assert.True(PanelVersionCommands.CreateAttempt(document, 1, id, 0, true, null, null, Now));
        var version = document.Panels[0].Versions.Last();
        Assert.Equal("Accepted 1", version.Prompt);
        Assert.Equal("Prepared 1", version.EffectivePrompt);
        Assert.Equal(PanelVersionCommands.OriginalVersionId, version.SourceVersionId);
        Assert.Equal(siblings, JsonSerializer.Serialize(document.Panels.Skip(1)));
        Assert.Equal(PanelGenerationStatus.Failed, document.Panels[0].Status);
        Assert.Equal(1, document.Revision);
        Assert.Equal(id.ToString("D"), document.PendingCommandId);
        Assert.Equal("panel", document.PendingCommandKind);
    }

    [Fact]
    public void DuplicateCommandsAreNoOps_ButReusedIdsAndStaleSubmissionsConflict()
    {
        var document = Board();
        var id = Guid.NewGuid();
        PanelVersionCommands.CreateAttempt(document, 2, id, 0, false, "Revised scene", null, Now);
        var saved = JsonSerializer.Serialize(document);
        Assert.False(PanelVersionCommands.CreateAttempt(document, 2, id, 0, false, "Revised scene", null, Now));
        Assert.Equal(saved, JsonSerializer.Serialize(document));
        Assert.Throws<PanelCommandConflictException>(() =>
            PanelVersionCommands.CreateAttempt(document, 2, id, 0, false, "Different scene", null, Now));
        Assert.Throws<PanelCommandConflictException>(() =>
            PanelVersionCommands.CreateAttempt(document, 3, id, 0, false, "Revised scene", null, Now));
        document.Status = GenerationStatus.Completed;
        Assert.Throws<PanelCommandConflictException>(() =>
            PanelVersionCommands.CreateAttempt(document, 2, Guid.NewGuid(), 0, false, "Another", null, Now));
    }

    [Fact]
    public void ExplicitSelectionRetainsPreviousArtwork_AndFencesOlderResults()
    {
        var document = Board();
        var id = Guid.NewGuid();
        var panel = document.Panels[1];
        var originalTrace = new AiGenerationTrace { Operation = "panel-image", TotalTokens = 0 };
        panel.Traces.Add(originalTrace);
        PanelVersionCommands.CreateAttempt(document, 2, id, 0, false, "Revised scene", null, Now);
        var candidate = panel.Versions.Last();
        candidate.Status = PanelGenerationStatus.Completed;
        candidate.ImageBlobName = "new-image.png";
        candidate.Traces.Add(new() { Operation = "panel-image", AttemptId = id.ToString("D") });
        Assert.Equal("panel-2.png", panel.ImageBlobName);
        Assert.NotSame(originalTrace, panel.Versions[0].Traces[0]);
        document.Status = GenerationStatus.Completed;
        document.PendingCommandId = null;
        var commandId = Guid.NewGuid();
        PanelVersionCommands.SelectVersion(document, 2, commandId, id, 1, Now);
        Assert.Equal("new-image.png", panel.ImageBlobName);
        Assert.Equal("panel-2.png", panel.Versions[0].ImageBlobName);
        Assert.Equal(2, document.SelectedRevision);
        Assert.Equal(0, document.ComposedRevision);
        Assert.False(PanelVersionCommands.Matches(document, id.ToString("D"), 1));
        Assert.False(PanelVersionCommands.SelectVersion(document, 2, commandId, id, 1, Now));
        Assert.NotSame(candidate.Traces[0], panel.Traces[0]);
        document.Status = GenerationStatus.Completed;
        PanelVersionCommands.SelectVersion(document, 2, Guid.NewGuid(), Guid.Empty, 2, Now);
        Assert.Equal("panel-2.png", panel.ImageBlobName);
        Assert.Equal(0, panel.Traces[0].TotalTokens);
    }

    [Fact]
    public void SafetyRejectionRequiresRevision_AndValidationHasNoSideEffects()
    {
        var document = Board();
        document.Panels[0].Issue = PanelGenerationIssue.ContentSafetyRejected;
        var saved = JsonSerializer.Serialize(document);
        Assert.Throws<ArgumentException>(() =>
            PanelVersionCommands.CreateAttempt(document, 1, Guid.NewGuid(), 0, true, null, null, Now));
        Assert.Equal(saved, JsonSerializer.Serialize(document));
        Assert.Throws<ArgumentException>(() =>
            PanelVersionCommands.CreateAttempt(document, 1, Guid.NewGuid(), 0, false, "Accepted 1", null, Now));
        Assert.Equal(saved, JsonSerializer.Serialize(document));
        PanelVersionCommands.CreateAttempt(document, 1, Guid.NewGuid(), 0, false, "Appropriately revised scene", null, Now);
        Assert.Equal("Appropriately revised scene", document.Panels[0].Versions.Last().EffectivePrompt);
        Assert.True(document.InputSnapshot!.RewritePrompt);
    }

    [Fact]
    public void ExecutionLeaseBlocksDuplicates_AndExpiredOwnershipCannotPublish()
    {
        var document = Board();
        PanelVersionCommands.CreateAttempt(document, 1, Guid.NewGuid(), 0, true, null, null, Now);
        var first = PanelVersionCommands.Claim(document, Now);
        Assert.True(PanelVersionCommands.OwnsExecution(document, first, Now));
        Assert.Throws<PanelCommandConflictException>(() => PanelVersionCommands.Claim(document, Now.AddMinutes(1)));
        Assert.False(PanelVersionCommands.OwnsExecution(document, first, Now + PanelVersionCommands.LeaseDuration));
        var second = PanelVersionCommands.Claim(document, Now + PanelVersionCommands.LeaseDuration);
        Assert.NotEqual(first, second);
        Assert.False(PanelVersionCommands.OwnsExecution(document, first, Now + PanelVersionCommands.LeaseDuration));
        Assert.True(PanelVersionCommands.ExecutionTimeout < PanelVersionCommands.LeaseDuration);
    }

    [Fact]
    public void LegacyContractsRemainReadable_ButCannotInventFrozenRetryInputs()
    {
        var message = JsonSerializer.Deserialize<BoardGenerationRequestMessage>("""{"BoardId":"legacy"}""")!;
        Assert.Equal(2, message.SchemaVersion);
        Assert.Null(message.CommandId);
        Assert.Null(message.Operation);
        var document = Board();
        document.InputSnapshot = null;
        Assert.Throws<ArgumentException>(() =>
            PanelVersionCommands.CreateAttempt(document, 1, Guid.NewGuid(), 0, true, null, null, Now));
    }

    private static GenerationDocument Board() => new()
    {
        Status = GenerationStatus.Completed,
        InputSnapshot = new()
        {
            RewritePrompt = true,
            Panels = Enumerable.Range(1, 6).Select(number => new GenerationInputPanel
                { Number = number, Prompt = $"Accepted {number}" }).ToList()
        },
        Panels = Enumerable.Range(1, 6).Select(number => new GenerationPanel
        {
            Number = number, Prompt = "Mutable projection", EffectivePrompt = $"Prepared {number}",
            Status = number == 1 ? PanelGenerationStatus.Failed : PanelGenerationStatus.Completed,
            ImageBlobName = number == 1 ? null : $"panel-{number}.png"
        }).ToList()
    };
}
