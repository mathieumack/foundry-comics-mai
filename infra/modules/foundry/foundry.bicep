// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the AI Search')
param name string

@description('Name of the project to associate with the Foundry account')
param projectName string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('Enable public network access')
param publicNetworkAccess string = 'Disabled'

@description('Name of Cosmos Db service to connect to Foundry')
param cosmosDBName string 

@description('Name of Azure Storage service to connect to Foundry')
param azureStorageName string 

@description('Name of Azure AI Search service to connect to Foundry')
param aiSearchName string

@description('Create the BYO project connections (Cosmos DB, Storage, AI Search). Once the Agents capability host is created it takes ownership of these connections and they can no longer be modified through the connections API. Set to false on subsequent re-provisions to avoid the "Connection is in use by the workspace capability host" error.')
param deployConnections bool = true

@description('Subnet ID for private endpoint')
param privateEndpointSubnetId string = ''

@description('Enable private endpoint')
param enablePrivateEndpoint bool = true

@description('Private DNS Zone ID for cognitive services')
param cognitiveServicesPrivateDnsZoneId string = ''

@description('Private DNS Zone ID for OpenAI')
param openaiPrivateDnsZoneId string = ''

@description('Private DNS Zone ID for AI Services')
param aiServicesPrivateDnsZoneId string = ''

resource foundry 'Microsoft.CognitiveServices/accounts@2026-03-01' = {
  name: name
  location: location
  sku: {
    name: 'S0'
  }
  kind: 'AIServices'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    apiProperties: {}
    customSubDomainName: name
    allowProjectManagement: true
    associatedProjects: []
    publicNetworkAccess: publicNetworkAccess
    disableLocalAuth: true // Force use of Azure AD authentication
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource guardrailNoLimits 'Microsoft.CognitiveServices/accounts/raiPolicies@2026-05-01' = {
  parent: foundry
  name: 'GuardrailNoLimits'
  properties: {
    mode: 'Default'
    basePolicyName: 'Microsoft.DefaultV2'
    contentFilters: [
      {
        name: 'Hate'
        severityThreshold: 'High'
        blocking: true
        enabled: true
        source: 'Prompt'
        action: 'NONE'
      }
      {
        name: 'Hate'
        severityThreshold: 'High'
        blocking: true
        enabled: true
        source: 'Completion'
        action: 'NONE'
      }
      {
        name: 'Sexual'
        severityThreshold: 'High'
        blocking: true
        enabled: true
        source: 'Prompt'
        action: 'NONE'
      }
      {
        name: 'Sexual'
        severityThreshold: 'High'
        blocking: true
        enabled: true
        source: 'Completion'
        action: 'NONE'
      }
      {
        name: 'Selfharm'
        severityThreshold: 'High'
        blocking: true
        enabled: true
        source: 'Prompt'
        action: 'NONE'
      }
      {
        name: 'Selfharm'
        severityThreshold: 'High'
        blocking: true
        enabled: true
        source: 'Completion'
        action: 'NONE'
      }
      {
        name: 'Violence'
        severityThreshold: 'High'
        blocking: true
        enabled: true
        source: 'Prompt'
        action: 'NONE'
      }
      {
        name: 'Violence'
        severityThreshold: 'High'
        blocking: true
        enabled: true
        source: 'Completion'
        action: 'NONE'
      }
      {
        name: 'Jailbreak'
        blocking: false
        enabled: false
        source: 'Prompt'
        action: 'NONE'
      }
      {
        name: 'Indirect Attack'
        blocking: false
        enabled: false
        source: 'Prompt'
        action: 'NONE'
      }
      {
        name: 'Protected Material Code'
        blocking: false
        enabled: false
        source: 'Completion'
        action: 'NONE'
      }
      {
        name: 'Protected Material Text'
        blocking: false
        enabled: false
        source: 'Completion'
        action: 'NONE'
      }
      {
        name: 'Indirect Attack Spotlighting'
        blocking: false
        enabled: false
        source: 'Prompt'
        action: 'NONE'
      }
    ]
  }
}

resource thinkingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-05-01' = {
  parent: foundry
  name: 'MAI-Thinking-1'
  sku: {
    name: 'GlobalStandard'
    capacity: 500
  }
  properties: {
    model: {
      format: 'Microsoft'
      name: 'MAI-Thinking-1'
      version: '2026-06-01'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
    raiPolicyName: 'GuardrailNoLimits'
  }
  dependsOn: [
    guardrailNoLimits
  ]
}

resource imageDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-05-01' = {
  parent: foundry
  name: 'MAI-Image-2.5-Pro'
  sku: {
    name: 'GlobalStandard'
    capacity: 2
  }
  properties: {
    model: {
      format: 'Microsoft'
      name: 'MAI-Image-2.5-Pro'
      version: '2026-06-19'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
    raiPolicyName: 'GuardrailNoLimits'
  }
  dependsOn: [
    thinkingDeployment
  ]
}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if(enablePrivateEndpoint){
  name: '${name}-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-pe-connection'
        properties: {
          privateLinkServiceId: foundry.id
          groupIds: [
            'account'
          ]
        }
      }
    ]
  }
  dependsOn: [
    imageDeployment
  ]
}

resource privateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (enablePrivateEndpoint && !empty(privateEndpointSubnetId) && !empty(cognitiveServicesPrivateDnsZoneId)) {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink-cognitiveservices-azure-com'
        properties: {
          privateDnsZoneId: cognitiveServicesPrivateDnsZoneId
        }
      }
      {
        name: 'privatelink-openai-azure-com'
        properties: {
          privateDnsZoneId: openaiPrivateDnsZoneId
        }
      }
      {
        name: 'privatelink-services-ai-azure-com'
        properties: {
          privateDnsZoneId: aiServicesPrivateDnsZoneId
        }
      }
    ]
  }
}


resource searchService 'Microsoft.Search/searchServices@2024-06-01-preview' existing = {
  name: aiSearchName
}
resource cosmosDBAccount 'Microsoft.DocumentDB/databaseAccounts@2024-12-01-preview' existing = {
  name: cosmosDBName
}
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: azureStorageName
}

// Project connections (AppInsights, AI Search, MCP tools) are defined in
// modules/foundry/foundry-project-connections.bicep and wired from main.bicep
// to avoid circular dependencies with the researcher API container app.

//// Project :
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundry
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    description: 'Document validation engine'
    displayName: projectName
  }
  dependsOn: [
    imageDeployment
  ]


  resource project_connection_cosmosdb_account 'connections@2025-04-01-preview' = if (deployConnections) {
    name: cosmosDBName
    properties: {
      category: 'CosmosDB'
      target: cosmosDBAccount.properties.documentEndpoint
      authType: 'AAD'
      metadata: {
        ApiType: 'Azure'
        ResourceId: cosmosDBAccount.id
        location: cosmosDBAccount.location
      }
    }
  }

  resource project_connection_azure_storage 'connections@2025-04-01-preview' = if (deployConnections) {
    name: azureStorageName
    properties: {
      category: 'AzureStorageAccount'
      target: storageAccount.properties.primaryEndpoints.blob
      authType: 'AAD'
      metadata: {
        ApiType: 'Azure'
        ResourceId: storageAccount.id
        location: storageAccount.location
      }
    }
  }

  resource project_connection_azureai_search 'connections@2025-04-01-preview' = if (deployConnections) {
    name: aiSearchName
    properties: {
      category: 'CognitiveSearch'
      target: 'https://${aiSearchName}.search.windows.net'
      authType: 'AAD'
      metadata: {
        ApiType: 'Azure'
        ResourceId: searchService.id
        location: searchService.location
      }
    }
  }
}

// The Agents capability host is created by modules/foundry/foundry-capability-host.bicep
// so that its deployment can be ordered AFTER the project managed identity has received the
// required control-plane roles (Cosmos DB Operator, Storage Blob Data Contributor, Search
// Index/Service Contributor). Creating it inline here would run before those role assignments
// and fail. See main.bicep for the ordering (pre-caphost roles -> caphost -> post-caphost roles).

// Return the BYO connection names so the capability host module can reference them.
output cosmosDBConnection string = cosmosDBName
output azureStorageConnection string = azureStorageName
output aiSearchConnection string = aiSearchName

output foundryId string = foundry.id
output foundryEndpoint string = foundry.properties.endpoint
output foundryPrincipalId string = foundry.identity.principalId
output foundryName string = foundry.name
output foundryProjectId string = project.id
output foundryProjectPrincipalId string = project.identity.principalId
output foundryProjectName string = project.name
output foundryProjectEndpoint string = 'https://${name}.services.ai.azure.com/api/projects/${projectName}'

// Internal workspace ID (32-char hex) used to scope post-capability-host role assignments.
#disable-next-line BCP053
output foundryProjectWorkspaceId string = project.properties.internalId

