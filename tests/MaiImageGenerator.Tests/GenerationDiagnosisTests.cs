using System.Text.Json;
using System.Text.Json.Serialization;
using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;
using WebGenerationResponse = MaiImageGenerator.Web.Models.GenerationResponse;
using WebGenerationSummary = MaiImageGenerator.Web.Models.GenerationSummaryResponse;

namespace MaiImageGenerator.Tests;

public sealed class GenerationDiagnosisTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedPartialResult_RestoresCountsAndPanelDiagnosisWithoutChangingCompleted(bool legacy)
    {
        var document = new GenerationDocument
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = "private-owner",
            Status = GenerationStatus.Completed,
            SchemaVersion = legacy ? 2 : 3,
            BoardBlobName = "board.png",
            Panels = Enumerable.Range(1, 6).Select(number => new GenerationPanel
            {
                Number = number,
                Status = number <= 4 ? PanelGenerationStatus.Completed : PanelGenerationStatus.Failed,
                ImageBlobName = number <= 4 ? $"panel-{number}.png" : null,
                Issue = legacy || number <= 4 ? null : number == 5
                    ? PanelGenerationIssue.ContentSafetyRejected : PanelGenerationIssue.TechnicalFailure,
                Error = number <= 4 ? null : "private-provider-response"
            }).ToList()
        };
        var options = new JsonSerializerOptions(JsonSerializerOptions.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var savedJson = JsonSerializer.Serialize(document, options);
        if (legacy)
        {
            savedJson = savedJson.Replace(",\"issue\":null", "");
        }
        var restored = JsonSerializer.Deserialize<GenerationDocument>(savedJson, options)!;
        var response = GenerationService.ToResponse(restored, new ProjectDocument());
        var json = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
        var web = JsonSerializer.Deserialize<WebGenerationResponse>(json, JsonSerializerOptions.Web)!;
        var summary = JsonSerializer.Deserialize<WebGenerationSummary>(
            JsonSerializer.Serialize(GenerationService.ToSummaryResponse(restored), JsonSerializerOptions.Web),
            JsonSerializerOptions.Web)!;

        Assert.Equal(GenerationStatus.Completed, response.Status);
        Assert.Equal(GenerationCompleteness.Partial, response.Completeness);
        Assert.Equal(4, response.CompletedPanelCount);
        Assert.Equal(2, response.FailedPanelCount);
        Assert.Equal(6, response.PanelCount);
        Assert.Equal(MaiImageGenerator.Web.Models.GenerationCompleteness.Partial, web.Completeness);
        Assert.Equal(web.CompletedPanelCount, summary.CompletedPanelCount);
        Assert.Equal(web.FailedPanelCount, summary.FailedPanelCount);
        Assert.Equal(web.Completeness, summary.Completeness);
        Assert.All(web.Panels.Take(4), panel => Assert.NotNull(panel.ImageUri));
        Assert.NotNull(web.BoardUri);
        Assert.All(web.Panels.Skip(4), panel =>
        {
            Assert.Null(panel.ImageUri);
            Assert.NotNull(panel.Error);
            Assert.NotNull(panel.Issue);
        });
        Assert.Equal(legacy ? PanelGenerationIssue.TechnicalFailure : PanelGenerationIssue.ContentSafetyRejected,
            response.Panels[4].Issue);
        Assert.DoesNotContain("private-provider-response", json);
        Assert.DoesNotContain("private-owner", json);
    }

    [Fact]
    public void CosmosModel_PersistsOptionalIssueWithoutChangingStatusSerialization()
    {
        using var context = new MaiImageGeneratorDbContext(
            new DbContextOptionsBuilder<MaiImageGeneratorDbContext>()
                .UseCosmos("https://localhost:8081", Convert.ToBase64String(new byte[64]), "diagnosis-tests")
                .Options);
        var panel = context.Model.FindEntityType(typeof(GenerationDocument))!
            .FindNavigation(nameof(GenerationDocument.Panels))!.TargetEntityType;
        var issue = panel.FindProperty(nameof(GenerationPanel.Issue))!;
        Assert.True(issue.IsNullable);
        Assert.Equal(typeof(string), issue.GetProviderClrType());
        Assert.Equal(typeof(string), panel.FindProperty(nameof(GenerationPanel.Status))!.GetProviderClrType());
        Assert.Equal(2, (int)GenerationStatus.Completed);
        Assert.Equal(2, (int)PanelGenerationStatus.Completed);
    }
}
