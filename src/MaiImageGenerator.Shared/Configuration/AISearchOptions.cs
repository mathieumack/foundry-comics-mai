// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.

namespace MaiImageGenerator.Jobs.BoardGeneration.Configuration;

/// <summary>Configuration for Azure AI Search knowledge base pipeline.</summary>
public class AISearchOptions
{
    public const string SectionName = "AzureAISearch";
    /// <summary>Azure AI Search service endpoint URL.</summary>
    public string Endpoint { get; set; } = string.Empty;
    /// <summary>Name of the search index.</summary>
    public string IndexName { get; set; } = "documents-kb-index";
    /// <summary>Name of the AI Search indexer.</summary>
    public string IndexerName { get; set; } = "processing-indexer";
    /// <summary>Name of the Foundry IQ knowledge base index (used by the hosted agent).</summary>
    public string KnowledgeBaseIndexName { get; set; } = "kb-advanced-index";
    /// <summary>Name of the Azure AI Search knowledge base for agentic retrieval (2025-11-01-preview API).</summary>
    public string KnowledgeBaseName { get; set; } = string.Empty;
    /// <summary>Name of the knowledge source within the knowledge base. Leave empty to use all configured sources.</summary>
    public string KnowledgeSourceName { get; set; } = string.Empty;
}
