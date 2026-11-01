using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Tests;

public sealed class GenerationResultTests
{
    [Theory]
    [InlineData(0, 0, GenerationCompleteness.None)]
    [InlineData(0, 6, GenerationCompleteness.None)]
    [InlineData(4, 2, GenerationCompleteness.Partial)]
    [InlineData(6, 0, GenerationCompleteness.Complete)]
    public void Completeness_IsIndependentOfProcessingState(int completed, int failed, GenerationCompleteness expected)
    {
        foreach (var status in Enum.GetValues<GenerationStatus>())
        {
            var document = new GenerationDocument
            {
                Status = status,
                Panels = Enumerable.Range(1, 6).Select(number => new GenerationPanel
                {
                    Status = number <= completed ? PanelGenerationStatus.Completed :
                        number <= completed + failed ? PanelGenerationStatus.Failed : PanelGenerationStatus.Pending,
                    ImageBlobName = number <= completed ? $"panel-{number}.png" : null
                }).ToList()
            };

            var result = GenerationResult.From(document);

            Assert.Equal(6, result.PanelCount);
            Assert.Equal(completed, result.CompletedPanelCount);
            Assert.Equal(failed, result.FailedPanelCount);
            Assert.Equal(expected, result.Completeness);
            Assert.Equal(status, document.Status);
        }
    }

    [Fact]
    public void MissingPanelArtifact_AndComposedPlaceholders_DoNotCountAsUsableImages()
    {
        var result = GenerationResult.From(new GenerationDocument
        {
            Status = GenerationStatus.Completed,
            BoardBlobName = "board-with-placeholders.png",
            Panels = [new GenerationPanel { Status = PanelGenerationStatus.Completed }]
        });

        Assert.Equal(0, result.CompletedPanelCount);
        Assert.Equal(GenerationCompleteness.None, result.Completeness);
        Assert.Equal(GenerationCompleteness.None, GenerationResult.From(new GenerationDocument()).Completeness);
    }

    [Theory]
    [InlineData(PanelGenerationIssue.ContentSafetyRejected, "Content safety")]
    [InlineData(PanelGenerationIssue.ProviderThrottled, "provider is busy")]
    [InlineData(PanelGenerationIssue.TechnicalFailure, "technical failure")]
    [InlineData(null, "technical failure")]
    public void FailedPanels_ExposeSafeActionableErrors(PanelGenerationIssue? issue, string expected)
    {
        var panel = new GenerationPanel
        {
            Status = PanelGenerationStatus.Failed,
            Issue = issue,
            Error = "private-provider-response"
        };
        var message = GenerationResult.GetPanelError(panel)!;

        Assert.Contains(expected, message);
        Assert.Contains("new version", message);
        Assert.DoesNotContain(panel.Error, message);
    }

    [Fact]
    public void ProcessingThrottle_IsNotATerminalFailure_AndCompletedPanelsIgnoreStaleIssues()
    {
        var panel = new GenerationPanel
        {
            Status = PanelGenerationStatus.Processing,
            Issue = PanelGenerationIssue.ProviderThrottled
        };
        Assert.Contains("automatic retry", GenerationResult.GetPanelError(panel));
        panel.Status = PanelGenerationStatus.Completed;
        Assert.Null(GenerationResult.GetIssue(panel));
        Assert.Null(GenerationResult.GetPanelError(panel));
    }
}
