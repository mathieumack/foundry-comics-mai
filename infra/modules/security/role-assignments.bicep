// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
targetScope = 'resourceGroup'

@description('Principal ID to assign roles to')
param principalId string

@description('User Principal ID to assign roles to')
param userPrincipalId string = ''

@description('Foundry Project Principal ID for ACR pull operations (Hosted Agents)')
param foundryProjectPrincipalId string = ''

@description('Foundry account (Cognitive Services) system-assigned Principal ID. The hosted-agent container may authenticate to AI Search with either the account or the project identity, so both need data-plane read access.')
param foundryAccountPrincipalId string = ''

@description('AI Search service name (for role assignment to call Foundry/OpenAI services)')
param aiSearchIdentityPrincipalId string = ''

@description('Foundry (Cognitive Services) account name for AI Search to call OpenAI services')
param foundryName string

@description('AI Search service name for role assignment to read index metadata')
param aiSearchName string

@description('CosmosDB account name for role assignment')
param cosmosDbAccountName string

@description('Storage Account name for role assignment')
param storageAccountName string

@description('Container Registry name hosting the hosted-agent image (for Foundry project AcrPull)')
param containerRegistryName string = ''

// Role definition IDs for built-in Azure RBAC roles
var cosmosDbAccountReaderRoleId = 'fbdf93bf-df7d-467e-a4d2-9458aa1360c8' // Cosmos DB Account Reader Role
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908' // Cognitive Services User
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' // Storage Blob Data Contributor
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
var storageFileDataSMBShareContributorRoleId = '0c867c2a-1d8c-454a-a3db-ab2ea1bdc8bb' // Storage File Data SMB Share Contributor
var azureAIUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d' // Azure AI User
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d' // AcrPull

resource cosmosDbAccount 'Microsoft.DocumentDB/databaseAccounts@2023-04-15' existing = if (!empty(cosmosDbAccountName)) {
  name: cosmosDbAccountName
}

// CosmosDB Account Reader role assignment (for reading metadata)
resource cosmosDbAccountReaderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(cosmosDbAccountName)) {
  name: guid(cosmosDbAccount.id, principalId, cosmosDbAccountReaderRoleId)
  scope: cosmosDbAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cosmosDbAccountReaderRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Account Blob Data Contributor role assignment
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = if (!empty(storageAccountName)) {
  name: storageAccountName
}

// Storage Account Blob Data Contributor role assignment for API managed identity
resource storageBlobDataContributorRoleAssignmentMI 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName)) {
  name: guid(storageAccount.id, principalId, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Account Queue Data Contributor role assignment for the API and job managed identity
resource storageQueueDataContributorRoleAssignmentMI 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName)) {
  name: guid(storageAccount.id, principalId, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Account Blob Data Contributor role assignment for User
resource storageBlobDataContributorRoleAssignmentUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(userPrincipalId)) {
  name: guid(storageAccount.id, userPrincipalId, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// Storage Account Blob Data Reader role assignment for AI Search managed identity
// Required so the AI Search blob indexer can read Markdown blobs from the 'processing' container.
var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1' // Storage Blob Data Reader

resource aiSearchStorageBlobDataReaderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(aiSearchIdentityPrincipalId)) {
  name: guid(storageAccount.id, aiSearchIdentityPrincipalId, storageBlobDataReaderRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: aiSearchIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Account Queue Data Contributor role assignment for User
resource storageQueueDataContributorRoleAssignmentUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(userPrincipalId)) {
  name: guid(storageAccount.id, userPrincipalId, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// Storage Account Table Data Contributor role assignment
// Storage Account Table Data Contributor role assignment for User
resource storageTableDataContributorRoleAssignmentUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(userPrincipalId)) {
  name: guid(storageAccount.id, userPrincipalId, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// Storage File Data SMB Share Contributor role assignment (for Azure File Share managed identity access)
// Storage File Data SMB Share Contributor role assignment for User
resource storageFileDataSMBShareContributorRoleAssignmentUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(userPrincipalId)) {
  name: guid(storageAccount.id, userPrincipalId, storageFileDataSMBShareContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageFileDataSMBShareContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// Foundry (Cognitive Services) Azure AI User role assignment
resource foundryAccount 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = if (!empty(foundryName)) {
  name: foundryName
}

// Foundry (Cognitive Services) Azure AI User role assignment for User
resource foundryAzureAIUserRoleAssignmentUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryName) && !empty(userPrincipalId)) {
  name: guid(foundryAccount.id, userPrincipalId, azureAIUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIUserRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// Foundry (Cognitive Services) Cognitive Services User role assignment for User.
// Required for the post-provision Content Understanding configuration step, which calls
// PATCH /contentunderstanding/defaults and needs the data action
// 'Microsoft.CognitiveServices/accounts/MultiModalIntelligence/defaults/write'.
// The Azure AI User role does not include this data action, whereas Cognitive Services User
// grants 'Microsoft.CognitiveServices/*' data actions.
resource foundryCognitiveServicesUserRoleAssignmentUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryName) && !empty(userPrincipalId)) {
  name: guid(foundryAccount.id, userPrincipalId, cognitiveServicesUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// API managed identity needs the same data action for Content Understanding calls.
resource foundryCognitiveServicesUserRoleAssignmentMI 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryName)) {
  name: guid(foundryAccount.id, principalId, cognitiveServicesUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// AI Search to Foundry Cognitive Services User role assignment (for embeddings and OpenAI calls)
resource aiSearchToFoundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryName) && !empty(aiSearchIdentityPrincipalId)) {
  name: guid(foundryAccount.id, aiSearchIdentityPrincipalId, cognitiveServicesUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: aiSearchIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Managed identity to AISearch service to create index or read and search data in AI Search :
resource aiSearchService 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: aiSearchName
}

var searchIndexDataContributor = '8ebe5a00-799e-43f5-93ac-243d3dce84a7' // Search Index Data Contributor

// AI Search Index Data Contributor role assignment for User
resource aiSearchIndexDataContributorRoleAssignmentUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(userPrincipalId)) {
  name: guid(aiSearchService.id, userPrincipalId, searchIndexDataContributor)
  scope: aiSearchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributor)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

var searchServiceContributor = '7ca78c08-252a-4471-8644-bb5ff32d4ba0' // Search Service Contributor role name

// AI Search Service Contributor role assignment for User
resource aiSearchServiceContributorRoleAssignmentUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(userPrincipalId)) {
  name: guid(aiSearchService.id, userPrincipalId, searchServiceContributor)
  scope: aiSearchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributor)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// ============================================
// FOUNDRY PROJECT IDENTITY ROLE ASSIGNMENTS
// ============================================
// All the same permissions as the managed identity (principalId) for the Foundry Project

// CosmosDB Account Reader role assignment for Foundry Project
resource cosmosDbAccountReaderRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(cosmosDbAccountName) && !empty(foundryProjectPrincipalId)) {
  name: guid(cosmosDbAccount.id, foundryProjectPrincipalId, cosmosDbAccountReaderRoleId)
  scope: cosmosDbAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cosmosDbAccountReaderRoleId)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Account Blob Data Contributor role assignment for Foundry Project
resource storageBlobDataContributorRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(foundryProjectPrincipalId)) {
  name: guid(storageAccount.id, foundryProjectPrincipalId, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Account Queue Data Contributor role assignment for Foundry Project
resource storageQueueDataContributorRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(foundryProjectPrincipalId)) {
  name: guid(storageAccount.id, foundryProjectPrincipalId, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Account Table Data Contributor role assignment for Foundry Project
resource storageTableDataContributorRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(foundryProjectPrincipalId)) {
  name: guid(storageAccount.id, foundryProjectPrincipalId, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Storage File Data SMB Share Contributor role assignment for Foundry Project
resource storageFileDataSMBShareContributorRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName) && !empty(foundryProjectPrincipalId)) {
  name: guid(storageAccount.id, foundryProjectPrincipalId, storageFileDataSMBShareContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageFileDataSMBShareContributorRoleId)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Foundry (Cognitive Services) Azure AI User role assignment for Foundry Project
resource foundryAzureAIUserRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryName) && !empty(foundryProjectPrincipalId)) {
  name: guid(foundryAccount.id, foundryProjectPrincipalId, azureAIUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIUserRoleId)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Container Registry AcrPull role assignment for Foundry Project (workspace) identity.
// Required so hosted agents can pull their container image from the registry. Without this,
// the agent fails to start with ImageError: "workspace managed identity has AcrPull permissions".
resource containerRegistryForFoundryProject 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = if (!empty(containerRegistryName)) {
  name: containerRegistryName
}

resource acrPullRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(containerRegistryName) && !empty(foundryProjectPrincipalId)) {
  name: guid(containerRegistryForFoundryProject.id, foundryProjectPrincipalId, acrPullRoleId)
  scope: containerRegistryForFoundryProject
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource acrPullRoleAssignmentApplication 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(containerRegistryName)) {
  name: guid(containerRegistryForFoundryProject.id, principalId, acrPullRoleId)
  scope: containerRegistryForFoundryProject
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// AI Search Index Data Contributor role assignment for Foundry Project
resource aiSearchIndexDataContributorRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryProjectPrincipalId)) {
  name: guid(aiSearchService.id, foundryProjectPrincipalId, searchIndexDataContributor)
  scope: aiSearchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributor)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// AI Search Service Contributor role assignment for Foundry Project
resource aiSearchServiceContributorRoleAssignmentFoundryProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryProjectPrincipalId)) {
  name: guid(aiSearchService.id, foundryProjectPrincipalId, searchServiceContributor)
  scope: aiSearchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributor)
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ============================================
// FOUNDRY ACCOUNT IDENTITY ROLE ASSIGNMENTS
// ============================================
// The hosted-agent container obtains a token from the Foundry runtime and may present
// EITHER the Foundry account's system-assigned identity OR the project identity when
// calling AI Search. Because the Search service has disableLocalAuth: true (RBAC only),
// the account identity also needs data-plane read access, otherwise SearchKnowledgeBase
// fails at runtime with 403 Forbidden.
var searchIndexDataReader = '1407120a-92aa-4202-b7e9-c0e197c71c8f' // Search Index Data Reader

resource aiSearchIndexDataReaderRoleAssignmentFoundryAccount 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryAccountPrincipalId)) {
  name: guid(aiSearchService.id, foundryAccountPrincipalId, searchIndexDataReader)
  scope: aiSearchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataReader)
    principalId: foundryAccountPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output cosmosDbAccountReaderRoleAssignmentId string = !empty(cosmosDbAccountName) ? cosmosDbAccountReaderRoleAssignment.id : ''
output aiSearchToFoundryRoleAssignmentId string = (!empty(foundryName) && !empty(aiSearchIdentityPrincipalId)) ? aiSearchToFoundryRoleAssignment.id : ''
