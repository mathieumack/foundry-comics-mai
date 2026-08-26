// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the Storage Account')
param name string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('Storage account SKU')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
  'Standard_RAGRS'
  'Premium_LRS'
])
param sku string = 'Standard_LRS'

@description('Storage account kind')
@allowed([
  'Storage'
  'StorageV2'
  'BlobStorage'
])
param kind string = 'StorageV2'

@description('Enable public network access')
param allowPublicAccess bool = false

@description('Enable blob public access')
param allowBlobPublicAccess bool = false

@description('Enable HTTPS only traffic')
param enableHttpsTrafficOnly bool = true

@description('Subnet ID for private endpoint')
param privateEndpointSubnetId string = ''

@description('Enable private endpoint')
param enablePrivateEndpoint bool = true

@description('Private DNS Zone ID for blob storage')
param blobPrivateDnsZoneId string = ''

@description('Private DNS Zone ID for queue storage')
param queuePrivateDnsZoneId string = ''

@description('Private DNS Zone ID for table storage')
param tablePrivateDnsZoneId string = ''

@description('Private DNS Zone ID for file storage')
param filePrivateDnsZoneId string = ''

@description('Enable versioning for blobs')
param enableBlobVersioning bool = true

@description('Principal ID for role assignments')
param principalId string = ''

@description('Principal type for role assignments')
@allowed([
  'Device'
  'ForeignGroup'
  'Group'
  'ServicePrincipal'
  'User'
])
param principalType string = 'ServicePrincipal'

@description('Containers that should be created within the storage account')
param containers array = []

@description('Queues that should be created within the storage account')
param queues array = []

@description('File shares that should be created within the storage account')
param fileShares array = []

@description('Subnet IDs that should be allowed to access the storage account')
param subnetsToAllow array = []

@description('Log Analytics workspace resource ID')
param logAnalyticsWorkspaceId string = ''

// Determine network default action based on access configuration
// If public access is allowed and no private endpoint, allow network access
// Otherwise, deny by default for security
var networkDefaultAction = (allowPublicAccess && !enablePrivateEndpoint) ? 'Allow' : 'Deny'

// Storage account resource
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
  }
  kind: kind
  properties: {
    allowBlobPublicAccess: allowBlobPublicAccess
    // Container Apps file share mounts still require account-key authentication.
    allowSharedKeyAccess: !empty(fileShares)
    supportsHttpsTrafficOnly: enableHttpsTrafficOnly
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      defaultAction: networkDefaultAction
      bypass: 'AzureServices'
      virtualNetworkRules: [for subnetId in subnetsToAllow: {
        id: subnetId
        action: 'Allow'
      }]
      ipRules: []
    }
    publicNetworkAccess: allowPublicAccess ? 'Enabled' : 'Disabled'
    allowCrossTenantReplication: false
    largeFileSharesState: 'Enabled'
  }
}

// Blob service configuration
resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    isVersioningEnabled: enableBlobVersioning
  }
}

// Queue service configuration
resource queueServices 'Microsoft.Storage/storageAccounts/queueServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {}
}

// Table service configuration
resource tableServices 'Microsoft.Storage/storageAccounts/tableServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {}
}

// File service configuration
resource fileServices 'Microsoft.Storage/storageAccounts/fileServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    shareDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    // Note: Advanced SMB protocol settings (versions, encryption) are not supported on Standard storage accounts
    // Container Apps will automatically negotiate the appropriate SMB protocol version (SMB 3.0+)
  }
}

// Private endpoint for blob service
resource privateEndpointBlob 'Microsoft.Network/privateEndpoints@2023-05-01' = if (enablePrivateEndpoint && !empty(privateEndpointSubnetId)) {
  name: '${name}-blob-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-blob-connection'
        properties: {
          privateLinkServiceId: storageAccount.id
          groupIds: ['blob']
        }
      }
    ]
  }
}

// Private endpoint for queue service
resource privateEndpointQueue 'Microsoft.Network/privateEndpoints@2023-05-01' = if (enablePrivateEndpoint && !empty(privateEndpointSubnetId)) {
  name: '${name}-queue-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-queue-connection'
        properties: {
          privateLinkServiceId: storageAccount.id
          groupIds: ['queue']
        }
      }
    ]
  }
}

// Private endpoint for table service
resource privateEndpointTable 'Microsoft.Network/privateEndpoints@2023-05-01' = if (enablePrivateEndpoint && !empty(privateEndpointSubnetId)) {
  name: '${name}-table-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-table-connection'
        properties: {
          privateLinkServiceId: storageAccount.id
          groupIds: ['table']
        }
      }
    ]
  }
}

// Private endpoint for file service
resource privateEndpointFile 'Microsoft.Network/privateEndpoints@2023-05-01' = if (enablePrivateEndpoint && !empty(privateEndpointSubnetId)) {
  name: '${name}-file-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-file-connection'
        properties: {
          privateLinkServiceId: storageAccount.id
          groupIds: ['file']
        }
      }
    ]
  }
}

// DNS zone group for blob private endpoint
resource dnsZoneGroupBlob 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = if (enablePrivateEndpoint && !empty(blobPrivateDnsZoneId)) {
  parent: privateEndpointBlob
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob-config'
        properties: {
          privateDnsZoneId: blobPrivateDnsZoneId
        }
      }
    ]
  }
}

// DNS zone group for queue private endpoint
resource dnsZoneGroupQueue 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = if (enablePrivateEndpoint && !empty(queuePrivateDnsZoneId)) {
  parent: privateEndpointQueue
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'queue-config'
        properties: {
          privateDnsZoneId: queuePrivateDnsZoneId
        }
      }
    ]
  }
}

// DNS zone group for table private endpoint
resource dnsZoneGroupTable 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = if (enablePrivateEndpoint && !empty(tablePrivateDnsZoneId)) {
  parent: privateEndpointTable
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'table-config'
        properties: {
          privateDnsZoneId: tablePrivateDnsZoneId
        }
      }
    ]
  }
}

// DNS zone group for file private endpoint
resource dnsZoneGroupFile 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = if (enablePrivateEndpoint && !empty(filePrivateDnsZoneId)) {
  parent: privateEndpointFile
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'file-config'
        properties: {
          privateDnsZoneId: filePrivateDnsZoneId
        }
      }
    ]
  }
}

// Now create containers :
resource storageContainers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = [for container in containers: {
  parent: blobServices
  name: container
  properties: {
    publicAccess: 'None'
  }
}]

// Now create queues :
resource storageQueues 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-01-01' = [for queue in queues: {
  parent: queueServices
  name: queue
  properties: {}
}]

// Now create file shares:
resource storageFileShares 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = [for share in fileShares: {
  parent: fileServices
  name: share.name
  properties: {
    accessTier: share.?accessTier ?? 'Hot'
    shareQuota: share.?quota ?? 100
    enabledProtocols: 'SMB'
    // Ensure proper permissions for Container Apps mounting
    // Note: rootSquash not set (default NoRootSquash) allows root access which Container Apps may need
  }
}]

// Role assignment for Storage Blob Data Contributor
resource roleAssignmentBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: storageAccount
  name: guid(storageAccount.id, principalId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: principalId
    principalType: principalType
  }
}

// Role assignment for Storage Queue Data Contributor
resource roleAssignmentQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: storageAccount
  name: guid(storageAccount.id, principalId, '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
    principalId: principalId
    principalType: principalType
  }
}

// Role assignment for Storage Table Data Contributor
resource roleAssignmentTableContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: storageAccount
  name: guid(storageAccount.id, principalId, '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
    principalId: principalId
    principalType: principalType
  }
}

// Role assignment for Storage File Data SMB Share Contributor
resource roleAssignmentFileContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: storageAccount
  name: guid(storageAccount.id, principalId, '0c867c2a-1d8c-454a-a3db-ab2ea1bdc8bb')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0c867c2a-1d8c-454a-a3db-ab2ea1bdc8bb')
    principalId: principalId
    principalType: principalType
  }
}

// Diagnostic settings
resource diagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(logAnalyticsWorkspaceId)) {
  name: 'diags'
  scope: storageAccount
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// Outputs
@description('Storage account ID')
output id string = storageAccount.id

@description('Storage account name')
output name string = storageAccount.name

@description('Primary blob endpoint')
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob

@description('Primary queue endpoint')
output queueEndpoint string = storageAccount.properties.primaryEndpoints.queue

@description('Primary table endpoint')
output tableEndpoint string = storageAccount.properties.primaryEndpoints.table

@description('Storage account hostname')
output hostname string = replace(storageAccount.properties.primaryEndpoints.blob, 'https://', '')

@description('Primary file endpoint')
output fileEndpoint string = storageAccount.properties.primaryEndpoints.file
