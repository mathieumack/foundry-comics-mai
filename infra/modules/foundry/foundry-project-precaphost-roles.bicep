// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
//
// Control-plane RBAC that MUST be granted to the Foundry project's system-assigned
// managed identity BEFORE the Agents capability host is created.
//
// NOTE: The remaining project-identity roles required by the capability host
// (Storage Blob Data Contributor, Search Index Data Contributor, Search Service
// Contributor, Cosmos DB Data Contributor) are already granted by
// modules/security/role-assignments.bicep. To avoid duplicate role assignments
// (RoleAssignmentExists — same principal/role/scope with a different name), this
// module only grants the one role that module does not cover: Cosmos DB Operator
// (needed by the capability host to provision the per-project thread databases).
// main.bicep makes the capability host depend on BOTH this module and
// role-assignments.bicep so all required roles exist before the caphost is created.
targetScope = 'resourceGroup'

@description('Name of the Cosmos DB account (project thread storage).')
param cosmosDBName string

@description('Principal ID of the Foundry project system-assigned managed identity.')
param projectPrincipalId string

// Built-in role definition ID
var cosmosDbOperatorRoleId = '230815da-be43-4aae-9cb4-875f7bd000aa' // Cosmos DB Operator

resource cosmosDBAccount 'Microsoft.DocumentDB/databaseAccounts@2024-12-01-preview' existing = {
  name: cosmosDBName
}

// Cosmos DB Operator on the Cosmos account
resource cosmosDbOperatorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: cosmosDBAccount
  name: guid(projectPrincipalId, cosmosDbOperatorRoleId, cosmosDBAccount.id)
  properties: {
    principalId: projectPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cosmosDbOperatorRoleId)
    principalType: 'ServicePrincipal'
  }
}
