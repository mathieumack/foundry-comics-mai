// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
//
// Creates the Agents capability host on the Foundry project. This module is intentionally
// separated from the project so its deployment can be ordered AFTER the project's managed
// identity has received the required control-plane roles (see
// foundry-project-precaphost-roles.bicep). The capability host wires the BYO Cosmos DB,
// Storage and AI Search connections into the Agent Service.
@description('Name of the Foundry (Cognitive Services) account.')
param accountName string

@description('Name of the Foundry project.')
param projectName string

@description('Name of the Cosmos DB project connection (thread storage).')
param cosmosDBConnection string

@description('Name of the Storage project connection (blob storage).')
param azureStorageConnection string

@description('Name of the AI Search project connection (vector store).')
param aiSearchConnection string

@description('Name of the capability host to create.')
param projectCapHost string = 'caphostproj'

@description('Name of the account-level capability host to create.')
param accountCapHost string = 'caphostacc'

@description('Create the Agents capability host. The capability host is immutable once created and takes ownership of the referenced connections. Set to false on subsequent re-provisions to avoid conflicts.')
param deployCapabilityHost bool = true

var threadConnections = [cosmosDBConnection]
var storageConnections = [azureStorageConnection]
var vectorStoreConnections = [aiSearchConnection]

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  name: projectName
  parent: account
}

// The account-level capability host must exist BEFORE the project-level capability
// host, otherwise deployment fails with "Foundry Account capabilityHost Not Found".
resource accountCapabilityHost 'Microsoft.CognitiveServices/accounts/capabilityHosts@2026-03-15-preview' = if (deployCapabilityHost) {
  name: accountCapHost
  parent: account
  properties: {
    capabilityHostKind: 'Agents'
  }
}

resource projectCapabilityHost 'Microsoft.CognitiveServices/accounts/projects/capabilityHosts@2026-03-15-preview' = if (deployCapabilityHost) {
  name: projectCapHost
  parent: project
  properties: {
    capabilityHostKind: 'Agents'
    vectorStoreConnections: vectorStoreConnections
    storageConnections: storageConnections
    threadStorageConnections: threadConnections
  }
  dependsOn: [
    accountCapabilityHost
  ]
}

output capabilityHostName string = deployCapabilityHost ? projectCapabilityHost.name : projectCapHost
