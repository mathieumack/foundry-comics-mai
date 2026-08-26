// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.

namespace MaiImageGenerator.Jobs.BoardGeneration.Configuration;

/// <summary>Configuration for Azure AI Foundry / OpenAI.</summary>
public class AzureFoundryOptions
{
    public const string SectionName = "AzureFoundry";
    public string Endpoint { get; set; } = string.Empty;
    public string ProjectEndpoint { get; set; } = string.Empty;
    public string ChatDeploymentName { get; set; } = "gpt-5.2";
    public string EmbeddingDeploymentName { get; set; } = "text-embedding-3-large";
}
