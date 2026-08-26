// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the AI Search')
param name string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('SKU for the AI Search service')
param sku string = 'standard' // Set standard as default to support private indexes and private endpoints

@description('Enable public network access')
param publicNetworkAccess string = 'Disabled'

@description('Log Analytics workspace resource ID')
param logAnalyticsWorkspaceId string

@description('Subnet ID for private endpoint')
param privateEndpointSubnetId string = ''

@description('Enable private endpoint')
param enablePrivateEndpoint bool = true

@description('Private DNS Zone ID for AI Search')
param privateDnsZoneId string = ''

// AI Search public access is managed dynamically via az rest in postprovision/postdeploy
// hooks: temporarily opened (Enabled + deployer IP rule) to run config scripts,
// then closed back to Disabled. The private endpoint handles all runtime VNet access.

resource searchService 'Microsoft.Search/searchServices@2026-03-01-preview' = {
  name: name
  location: location
  sku: {
    name: sku
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    endpoint: 'https://${name}.search.windows.net'
    computeType: 'Default'
    publicNetworkAccess: publicNetworkAccess
    networkRuleSet: {
      ipRules: []
      bypass: 'AzureServices'
    }
    encryptionWithCmk: {
      enforcement: 'Unspecified'
    }
    disableLocalAuth: true
    dataExfiltrationProtections: []
    semanticSearch: 'free'
    upgradeAvailable: 'notAvailable'
  }
}

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
          privateLinkServiceId: searchService.id
          groupIds: [
            'searchService'
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
        name: 'privatelink-search-windows-net'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

resource diagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diags'
  scope: searchService
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        category: 'OperationLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

output searchServiceId string = searchService.id
output searchServiceName string = searchService.name
output searchServiceEndpoint string = searchService.properties.endpoint
output searchServicePrincipalId string = searchService.identity.principalId
