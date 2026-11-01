using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Shared.Services;

public static class PanelVersionCommands
{
    public static readonly string OriginalVersionId = Guid.Empty.ToString("D");
    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    public static bool CreateAttempt(GenerationDocument document, int panelNumber, Guid attemptId,
        long expectedRevision, bool retry, string? prompt, Guid? sourceVersionId, DateTimeOffset now)
    {
        ValidateId(attemptId);
        var panel = FindPanel(document, panelNumber);
        var id = attemptId.ToString("D");
        var sourceId = retry ? (sourceVersionId?.ToString("D") ?? OriginalVersionId) : null;
        var existing = document.Panels.SelectMany(item => item.Versions).SingleOrDefault(item => item.Id == id);
        if (existing is not null)
        {
            if (!panel.Versions.Contains(existing) || existing.IsRetry != retry ||
                existing.SourceVersionId != sourceId || (retry && !string.IsNullOrWhiteSpace(prompt)) ||
                (!retry && existing.Prompt != prompt?.Trim()))
                throw new PanelCommandConflictException("This attempt ID was already used with different inputs.");
            return false;
        }
        EnsureEditable(document, expectedRevision);
        if (document.SelectionCommands.Any(item => item.Id == id))
            throw new PanelCommandConflictException("This command ID was already used.");
        if (retry && !string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Retry uses frozen inputs; use a new version to revise the prompt.");
        if (!retry && (string.IsNullOrWhiteSpace(prompt) || prompt.Trim().Length > 4000))
            throw new ArgumentException("A revised panel prompt of at most 4000 characters is required.");

        var source = retry ? panel.Versions.SingleOrDefault(item => item.Id == sourceId) ??
            (panel.Versions.Count == 0 && sourceId == OriginalVersionId ? Original(document, panel) : null) : null;
        if (retry && (source is null || source.Status != PanelGenerationStatus.Failed))
            throw new ArgumentException("Only a failed panel version can be retried.");
        if (retry && source!.Issue == PanelGenerationIssue.ContentSafetyRejected)
            throw new ArgumentException("Content safety rejection requires an appropriate prompt revision.");
        if (!retry && (panel.Versions.Any(item =>
                item.Issue == PanelGenerationIssue.ContentSafetyRejected && item.Prompt == prompt!.Trim()) ||
            (panel.Versions.Count == 0 && panel.Issue == PanelGenerationIssue.ContentSafetyRejected &&
             Original(document, panel).Prompt == prompt!.Trim())))
            throw new ArgumentException("Revise the rejected scene before creating another version.");

        PreserveOriginal(document, panel);
        panel.Versions.Add(new PanelVersion
        {
            Id = id, IsRetry = retry, SourceVersionId = sourceId,
            Prompt = retry ? source!.Prompt : prompt!.Trim(),
            EffectivePrompt = retry ? source!.EffectivePrompt : prompt!.Trim(),
            Status = PanelGenerationStatus.Pending, CreatedAt = now
        });
        BeginCommand(document, id, "panel", panelNumber, now);
        return true;
    }

    public static bool SelectVersion(GenerationDocument document, int panelNumber, Guid commandId,
        Guid versionId, long expectedRevision, DateTimeOffset now)
    {
        ValidateId(commandId);
        var id = commandId.ToString("D");
        var versionKey = versionId.ToString("D");
        var previous = document.SelectionCommands.SingleOrDefault(item => item.Id == id);
        if (previous is not null)
        {
            if (previous.PanelNumber != panelNumber || previous.VersionId != versionKey)
                throw new PanelCommandConflictException("This selection ID was already used with different inputs.");
            return false;
        }
        EnsureEditable(document, expectedRevision);
        if (document.Panels.SelectMany(item => item.Versions).Any(item => item.Id == id))
            throw new PanelCommandConflictException("This command ID was already used.");
        var panel = FindPanel(document, panelNumber);
        var version = panel.Versions.SingleOrDefault(item => item.Id == versionKey) ??
            (panel.Versions.Count == 0 && versionKey == OriginalVersionId ? Original(document, panel) : null);
        if (version is null || version.Status != PanelGenerationStatus.Completed || version.ImageBlobName is null)
            throw new ArgumentException("Select a successfully generated panel version.");
        PreserveOriginal(document, panel);
        panel.SelectedVersionId = version.Id;
        panel.Prompt = version.Prompt;
        panel.EffectivePrompt = version.EffectivePrompt;
        panel.ImageBlobName = version.ImageBlobName;
        panel.Status = PanelGenerationStatus.Completed;
        panel.Issue = null;
        panel.Error = null;
        panel.Traces = version.Traces.Select(CloneTrace).ToList();
        document.SelectionCommands.Add(new() { Id = id, PanelNumber = panelNumber, VersionId = version.Id });
        BeginCommand(document, id, "compose", panelNumber, now);
        document.SelectedRevision = document.Revision;
        return true;
    }

    public static bool Matches(GenerationDocument document, string? commandId, long? revision) =>
        commandId is not null && document.PendingCommandId == commandId && document.Revision == revision &&
        document.Status is GenerationStatus.Pending or GenerationStatus.Processing;

    public static bool HasActiveLease(GenerationDocument document, DateTimeOffset now) =>
        document.CommandExecutionId is not null && document.CommandLeaseExpiresAt > now;

    public static bool OwnsExecution(GenerationDocument document, string executionId, DateTimeOffset now) =>
        document.CommandExecutionId == executionId && document.CommandLeaseExpiresAt > now;

    public static string Claim(GenerationDocument document, DateTimeOffset now)
    {
        if (HasActiveLease(document, now))
            throw new PanelCommandConflictException("This panel command is already executing.");
        document.CommandExecutionId = Guid.NewGuid().ToString("D");
        document.CommandLeaseExpiresAt = now + LeaseDuration;
        document.Status = GenerationStatus.Processing;
        document.UpdatedAt = now;
        return document.CommandExecutionId;
    }

    private static void EnsureEditable(GenerationDocument document, long expectedRevision)
    {
        if (document.InputSnapshot is null)
            throw new ArgumentException("This legacy board has no retained inputs. Create a new board version first.");
        if (document.Revision != expectedRevision || document.Status is GenerationStatus.Pending or GenerationStatus.Processing or GenerationStatus.Canceled)
            throw new PanelCommandConflictException("The board changed or is still processing. Refresh before trying again.");
    }

    private static void BeginCommand(GenerationDocument document, string id, string kind, int panelNumber, DateTimeOffset now)
    {
        document.SchemaVersion = 5;
        document.Revision++;
        document.PendingCommandId = id;
        document.PendingCommandKind = kind;
        document.PendingPanelNumber = panelNumber;
        document.CommandExecutionId = null;
        document.CommandLeaseExpiresAt = null;
        document.Status = GenerationStatus.Pending;
        document.Error = null;
        document.UpdatedAt = now;
    }

    private static GenerationPanel FindPanel(GenerationDocument document, int number) =>
        document.Panels.SingleOrDefault(item => item.Number == number)
        ?? throw new ArgumentException("The panel was not found.");

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty command ID is required.");
    }

    private static void PreserveOriginal(GenerationDocument document, GenerationPanel panel)
    {
        if (panel.Versions.Count > 0) return;
        panel.Versions.Add(Original(document, panel));
        panel.SelectedVersionId = OriginalVersionId;
    }

    private static PanelVersion Original(GenerationDocument document, GenerationPanel panel) => new()
        {
            Id = OriginalVersionId,
            Prompt = document.InputSnapshot!.Panels.Single(item => item.Number == panel.Number).Prompt,
            EffectivePrompt = panel.EffectivePrompt, Status = panel.Status, ImageBlobName = panel.ImageBlobName,
            Issue = GenerationResult.GetIssue(panel), Traces = panel.Traces.Select(CloneTrace).ToList(),
            CreatedAt = document.CreatedAt
        };

    private static AiGenerationTrace CloneTrace(AiGenerationTrace trace) => new()
    {
        Operation = trace.Operation, Provider = trace.Provider, Model = trace.Model, RequestId = trace.RequestId,
        AttemptId = trace.AttemptId, InputTokens = trace.InputTokens, OutputTokens = trace.OutputTokens,
        TotalTokens = trace.TotalTokens, UsageSource = trace.UsageSource, StartedAt = trace.StartedAt,
        CompletedAt = trace.CompletedAt, DurationMs = trace.DurationMs, ImageCount = trace.ImageCount,
        ImageSize = trace.ImageSize, ImageQuality = trace.ImageQuality, Status = trace.Status
    };
}

public sealed class PanelCommandConflictException(string message) : Exception(message);
