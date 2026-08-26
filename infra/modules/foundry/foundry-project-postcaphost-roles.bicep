// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
//
// Data-plane RBAC that MUST be granted to the Foundry project's system-assigned managed
// identity AFTER the Agents capability host has been created (the capability host provisions
// the per-project agent containers/databases these roles are scoped to):
//   - Storage Blob Data Owner, conditioned to the project's `*-azureml-agent` blob containers.
//   - Cosmos DB Built-in Data Contributor (SQL data plane) on the Cosmos account.
targetScope = 'resourceGroup'

@description('Name of the Storage account (project blob storage).')
param azureStorageName string

@description('Name of the Cosmos DB account (project thread storage).')
param cosmosDBName string

@description('Principal ID of the Foundry project system-assigned managed identity.')
param projectPrincipalId string

@description('The Foundry project workspace ID in dashed GUID form.')
param projectWorkspaceId string

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: azureStorageName
}

resource cosmosDBAccount 'Microsoft.DocumentDB/databaseAccounts@2024-12-01-preview' existing = {
  name: cosmosDBName
}

// Storage Blob Data Owner (b7e6dc6d-f1e8-4753-8033-0f276bb0955b), scoped by condition to the
// project's agent containers only, so the project identity cannot access unrelated blobs.
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var blobOwnerCondition = '((!(ActionMatches{\'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read\'})  AND  !(ActionMatches{\'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/filter/action\'}) AND  !(ActionMatches{\'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/write\'}) ) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringStartsWithIgnoreCase \'${projectWorkspaceId}\' AND @Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringLikeIgnoreCase \'*-azureml-agent\'))'

resource storageBlobDataOwnerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccount
  name: guid(storageAccount.id, projectPrincipalId, storageBlobDataOwnerRoleId, projectWorkspaceId)
  properties: {
    principalId: projectPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalType: 'ServicePrincipal'
    conditionVersion: '2.0'
    condition: blobOwnerCondition
  }
}

// Cosmos DB Built-in Data Contributor (SQL data plane role) on the account
var cosmosDataContributorRoleId = resourceId(
  'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions',
  cosmosDBName,
  '00000000-0000-0000-0000-000000000002'
)
var cosmosAccountScope = '/subscriptions/${subscription().subscriptionId}/resourceGroups/${resourceGroup().name}/providers/Microsoft.DocumentDB/databaseAccounts/${cosmosDBName}'

resource cosmosDataContributorAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2022-05-15' = {
  parent: cosmosDBAccount
  name: guid(projectWorkspaceId, cosmosDBName, cosmosDataContributorRoleId, projectPrincipalId)
  properties: {
    principalId: projectPrincipalId
    roleDefinitionId: cosmosDataContributorRoleId
    scope: cosmosAccountScope
  }
}
