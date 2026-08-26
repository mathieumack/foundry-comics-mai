namespace MaiImageGenerator.Web.Models;

public sealed class ComicPanelDraft
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Prompt { get; set; } = string.Empty;
    public IEnumerable<string> CharacterIds { get; set; } = [];
    public string? EffectivePrompt { get; set; }
    public string? ImageDataUrl { get; set; }
    public PanelUiStatus Status { get; set; }
}

public sealed class CharacterDraft
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<UploadedReference> References { get; } = [];
}

public sealed record UploadedReference(
    string ReferenceId,
    string FileName);

public enum PanelUiStatus
{
    Pending,
    Generating,
    Ready,
    Error
}