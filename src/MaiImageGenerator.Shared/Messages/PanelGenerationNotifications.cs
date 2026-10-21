namespace MaiImageGenerator.Shared.Messages;

public static class PanelGenerationNotifications
{
    public const string Started = "panel-generation-started";
    public const string Completed = "panel-generation-completed";
}

public sealed record PanelGenerationStarted(
    string OwnerId,
    string BoardId,
    int PanelNumber,
    string? TraceParent = null);

public sealed record PanelGenerationCompleted(
    string OwnerId,
    string BoardId,
    int PanelNumber,
    string? TraceParent = null);
