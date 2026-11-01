namespace MaiImageGenerator.Shared.Data.Entities;

public sealed record GenerationResult(
    int PanelCount,
    int CompletedPanelCount,
    int FailedPanelCount,
    GenerationCompleteness Completeness)
{
    public static GenerationResult From(GenerationDocument document)
    {
        var completed = document.Panels.Count(panel =>
            panel.Status == PanelGenerationStatus.Completed && !string.IsNullOrWhiteSpace(panel.ImageBlobName));
        var total = document.Panels.Count;
        return new(total, completed, document.Panels.Count(panel => panel.Status == PanelGenerationStatus.Failed),
            completed == 0 ? GenerationCompleteness.None :
            completed == total ? GenerationCompleteness.Complete : GenerationCompleteness.Partial);
    }

    public static PanelGenerationIssue? GetIssue(GenerationPanel panel) =>
        panel.Status == PanelGenerationStatus.Completed ? null :
        panel.Issue ?? (panel.Status == PanelGenerationStatus.Failed ? PanelGenerationIssue.TechnicalFailure : null);

    public static string? GetPanelError(GenerationPanel panel) => GetIssue(panel) switch
    {
        PanelGenerationIssue.ContentSafetyRejected =>
            "Content safety rejected this image. Review the scene and references before creating a new version.",
        PanelGenerationIssue.ProviderThrottled when panel.Status == PanelGenerationStatus.Processing =>
            "The image provider is throttling requests. Waiting before an automatic retry.",
        PanelGenerationIssue.ProviderThrottled =>
            "The image provider is busy. Try creating a new version later.",
        PanelGenerationIssue.TechnicalFailure =>
            "This image could not be generated because of a technical failure. Try creating a new version.",
        _ => null
    };
}
