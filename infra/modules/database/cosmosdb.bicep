// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name for the CosmosDB account')
param name string

@description('Location for the CosmosDB account')
param location string = resourceGroup().location

@description('Tags to apply to the resource')
param tags object = {}

@description('NoSQL database name')
param databaseName string = 'core'

@description('Enable public network access')
param publicNetworkAccess string = 'Enabled'

@description('Subnet ID for private endpoint')
param privateEndpointSubnetId string = ''

@description('Enable private endpoint')
param enablePrivateEndpoint bool = false

@description('Private DNS Zone ID for CosmosDB')
param privateDnsZoneId string = ''

@description('Principal ID of the managed identity to assign Cosmos DB roles to')
param managedIdentityPrincipalId string = ''

@description('Container names to create')
param containerNames array = []

@description('Partition key path for containers')
param partitionKeyPath string = '/ownerId'

// Cosmos DB Serverless supports only a single region
var locations = [{
  locationName: location
  failoverPriority: 0
  isZoneRedundant: false
}]

resource cosmosDbAccount 'Microsoft.DocumentDB/databaseAccounts@2023-11-15' = {
  name: name
  location: location
  tags: union(tags, {
    defaultExperience: 'Core (SQL)'
    'hidden-workload-type': 'Production'
  })
  kind: 'GlobalDocumentDB'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publicNetworkAccess: publicNetworkAccess
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    isVirtualNetworkFilterEnabled: false
    virtualNetworkRules: []
    disableKeyBasedMetadataWriteAccess: false
    enableFreeTier: false
    enableAnalyticalStorage: false
    databaseAccountOfferType: 'Standard'
    defaultIdentity: 'FirstPartyIdentity'
    networkAclBypass: 'None'
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
    capacity: {
      totalThroughputLimit: -1 // -1 enables serverless mode
    }
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
      maxIntervalInSeconds: 5
      maxStalenessPrefix: 100
    }
    locations: locations
    cors: []
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    ipRules: []
    backupPolicy: {
      type: 'Continuous'
      continuousModeProperties: {
        tier: 'Continuous30Days'
      }
    }
    networkAclBypassResourceIds: []
  }
}

// Create NoSQL database
resource sqlDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2023-11-15' = {
  parent: cosmosDbAccount
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
  }
}

resource containers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-11-15' = [for containerName in containerNames: {
  parent: sqlDatabase
  name: containerName
  properties: {
    resource: {
      id: containerName
      partitionKey: {
        paths: [partitionKeyPath]
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'Consistent'
        automatic: true
        includedPaths: [
          { path: '/ownerId/?' }
          { path: '/status/?' }
          { path: '/createdAt/?' }
          { path: '/updatedAt/?' }
        ]
        excludedPaths: [
          { path: '/*' }
          { path: '/panels/[]/prompt/?' }
          { path: '/panels/[]/effectivePrompt/?' }
          { path: '/_etag/?' }
        ]
      }
    }
  }
}]

// Cosmos DB native RBAC role assignment for Managed Identity
// When disableLocalAuth is true, you MUST use Cosmos DB native RBAC (sqlRoleAssignments)
// instead of Azure RBAC for data plane access
// Built-in Data Contributor Role ID: 00000000-0000-0000-0000-000000000002
var cosmosDbDataContributorRoleDefinitionId = '00000000-0000-0000-0000-000000000002'

resource sqlRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2023-11-15' = if (!empty(managedIdentityPrincipalId)) {
  name: guid(cosmosDbDataContributorRoleDefinitionId, managedIdentityPrincipalId, cosmosDbAccount.id)
  parent: cosmosDbAccount
  properties: {
    principalId: managedIdentityPrincipalId
    roleDefinitionId: '${cosmosDbAccount.id}/sqlRoleDefinitions/${cosmosDbDataContributorRoleDefinitionId}'
    scope: cosmosDbAccount.id
  }
}

// Private endpoint for Cosmos DB (optional)
resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if (enablePrivateEndpoint && !empty(privateEndpointSubnetId)) {
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
          privateLinkServiceId: cosmosDbAccount.id
          groupIds: [
            'Sql'
          ]
        }
      }
    ]
  }
}

resource privateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (enablePrivateEndpoint && !empty(privateEndpointSubnetId) && !empty(privateDnsZoneId)) {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink-documents-azure-com'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

@description('The resource ID of the CosmosDB account')
output cosmosDbAccountId string = cosmosDbAccount.id

@description('The name of the CosmosDB account')
output cosmosDbAccountName string = cosmosDbAccount.name

@description('The document endpoint URL of the CosmosDB account')
output cosmosDbEndpoint string = cosmosDbAccount.properties.documentEndpoint

@description('The NoSQL database name')
output databaseName string = databaseName

@description('The principal ID of the Cosmos DB system assigned identity')
output cosmosDbPrincipalId string = cosmosDbAccount.identity.principalId
