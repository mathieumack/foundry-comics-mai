using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using Azure.Core;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Jobs.BoardGeneration.Services;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.Extensions.Options;
using OpenAI;

namespace MaiImageGenerator.Tests;

public sealed class BoardPanelDiagnosisTests
{
    [Theory]
    [InlineData(400, "text", PanelGenerationIssue.ContentSafetyRejected)]
    [InlineData(400, "edit", PanelGenerationIssue.ContentSafetyRejected)]
    [InlineData(400, "rewrite", PanelGenerationIssue.ContentSafetyRejected)]
    [InlineData(429, "text", PanelGenerationIssue.ProviderThrottled)]
    [InlineData(429, "edit", PanelGenerationIssue.ProviderThrottled)]
    [InlineData(429, "rewrite", PanelGenerationIssue.ProviderThrottled)]
    [InlineData(503, "text", PanelGenerationIssue.TechnicalFailure)]
    [InlineData(503, "edit", PanelGenerationIssue.TechnicalFailure)]
    [InlineData(503, "rewrite", PanelGenerationIssue.TechnicalFailure)]
    public async Task WorkerProviderFailures_ProduceSafeClassifiedPanelDiagnosis(
        int status, string operation, PanelGenerationIssue expected)
    {
        using var http = new HttpClient(new FailureHandler(status));
        var client = CreateClient(http);
        var exception = await Assert.ThrowsAnyAsync<FoundryGenerationException>(async () =>
        {
            if (operation == "rewrite")
                await client.RewritePromptAsync(null, "Synthetic scene", CancellationToken.None);
            else
                await client.GenerateAsync("Synthetic scene", operation == "edit" ? [1] : null, false, CancellationToken.None);
        });
        var classify = typeof(GenerationService).GetMethod("GetPanelIssue", BindingFlags.Static | BindingFlags.NonPublic)!;
        var issue = (PanelGenerationIssue)classify.Invoke(null, [exception])!;
        var panel = new GenerationPanel { Status = PanelGenerationStatus.Failed, Issue = issue };

        Assert.Equal(expected, issue);
        Assert.DoesNotContain("private-provider-response", GenerationResult.GetPanelError(panel));
        Assert.DoesNotContain("private-provider-response", exception.Message);
        if (status == 429)
        {
            Assert.Equal(429, exception.Status);
            Assert.Equal(TimeSpan.FromMilliseconds(1), exception.RetryAfter);
        }
    }

    [Fact]
    public void StorageAndMalformedResponseFailures_AreTechnical()
    {
        var classify = typeof(GenerationService).GetMethod("GetPanelIssue", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var exception in new Exception[]
                 {
                     new Azure.RequestFailedException(503, "private-storage-response"),
                     new System.Text.Json.JsonException("private-provider-response"),
                     new FormatException("private-provider-response"),
                     new TimeoutException("private-provider-response")
                 })
        {
            Assert.Equal(PanelGenerationIssue.TechnicalFailure, classify.Invoke(null, [exception]));
        }
    }

    private static FoundryImageClient CreateClient(HttpClient http)
    {
        var openAI = new OpenAIClient(new ApiKeyCredential("unused-test-value"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.test/openai/v1/"),
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0)
        });
        return new(openAI, openAI.GetImageClient("test-image"), http, new FakeCredential(),
            Options.Create(new FoundryOptions
            {
                Endpoint = "https://example.test",
                PromptModel = "test-prompt",
                ImageModel = "test-image"
            }));
    }

    private sealed class FailureHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var code = status == 400 ? "content_safety_violation" : "provider_error";
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                    new { error = new { code, message = "private-provider-response" } }),
                    System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("retry-after-ms", "1");
            return Task.FromResult(response);
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) =>
            new("unused-test-value", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) =>
            ValueTask.FromResult(GetToken(context, token));
    }
}
