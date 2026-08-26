// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.

@description('Name of the existing Foundry (AIServices) account')
param foundryAccountName string

@description('Name of the existing Foundry project')
param projectName string

@description('Resource ID of the Application Insights instance to connect')
param applicationInsightsResourceId string

@secure()
@description('Application Insights connection string for the Foundry project connection')
param applicationInsightsConnectionString string

@description('Display name of the Application Insights instance')
param applicationInsightsName string

// Reference the existing Foundry account and project
resource foundry 'Microsoft.CognitiveServices/accounts@2025-10-01-preview' existing = {
  name: foundryAccountName
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  parent: foundry
  name: projectName
}

// ---------------------------------------------------------------------------
// Connection: Application Insights — native agent monitoring & tracing
// ---------------------------------------------------------------------------
resource connectionAppInsights 'Microsoft.CognitiveServices/accounts/projects/connections@2025-10-01-preview' = {
  parent: project
  name: 'appi-monitoring'
  properties: {
    authType: 'ApiKey'
    credentials: {
      key: applicationInsightsConnectionString
    }
    category: 'AppInsights'
    target: applicationInsightsResourceId
    useWorkspaceManagedIdentity: false
    isSharedToAll: false
    sharedUserList: []
    peRequirement: 'NotRequired'
    peStatus: 'NotApplicable'
    metadata: {
      displayName: applicationInsightsName
      ApiType: 'Azure'
      ResourceId: applicationInsightsResourceId
    }
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------
output connectionAppInsightsName string = connectionAppInsights.name
