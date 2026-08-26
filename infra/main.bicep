// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
targetScope = 'resourceGroup'

@minLength(1)
@maxLength(64)
@description('Name of the environment')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

@description('Principal ID of the deploying user to grant access to Azure resources. Auto-detected by predeploy scripts.')
param deployerPrincipalId string

@description('Deploy the Foundry BYO connections and Agents capability host. These resources are immutable once created: after the first successful deployment the connections become owned by the capability host and can no longer be modified through the connections API. Set to false (via azd env set DEPLOY_FOUNDRY_CAPABILITY_HOST false) on subsequent re-provisions to avoid the "Connection is in use by the workspace capability host" error.')
param deployFoundryCapabilityHost bool = true

@description('Container image for the API workload')
param apiContainerImage string

@description('Container image for the board generation job')
param generationJobContainerImage string

// Generate unique suffix based on resource group
var suffix = substring(uniqueString(resourceGroup().id, environmentName), 0, 3)

var currentUserPrincipalId = deployerPrincipalId
var abbrs = loadJsonContent('abbreviations.json')
var normalizedLocation = toLower(replace(location, ' ', ''))
var locationAbbreviation = abbrs.locationAbbreviations[normalizedLocation]
var regionalSuffix = '${locationAbbreviation}-${suffix}'
var compactRegionalSuffix = '${locationAbbreviation}${suffix}'
var staticWebAppLocation = 'westeurope'
var staticWebAppLocationAbbreviation = abbrs.locationAbbreviations[staticWebAppLocation]

var tags = {
  'azd-env-name': environmentName
  workload: 'foundry-iq-agent'
}

var apiTags = union(tags, {
  workload: 'comics'
})

var webTags = union(apiTags, {
  'azd-service-name': 'web'
})

var entraApiDisplayName = 'Comics API (${environmentName})'
var entraApiAudience = 'api://comics-${environmentName}'
var generationJobsQueueName = 'generation-jobs'
var generationJobsPoisonQueueName = 'generation-jobs-poison'

//=============================================================================
// MONITORING
//=============================================================================

module logAnalytics 'modules/monitoring/log-analytics.bicep' = {
  name: 'log-analytics'
  params: {
    name: '${abbrs.logAnalyticsWorkspace}${environmentName}-${regionalSuffix}'
    location: location
    tags: tags
  }
}

module appInsights 'modules/monitoring/application-insights.bicep' = {
  name: 'app-insights'
  params: {
    name: '${abbrs.applicationInsights}${environmentName}-${regionalSuffix}'
    location: location
    tags: tags
    workspaceResourceId: logAnalytics.outputs.id
  }
}

// =============================================================================
// SECURITY & IDENTITY
// =============================================================================

module managedIdentity 'modules/security/managed-identity.bicep' = {
  name: 'managed-identity'
  params: {
    name: '${abbrs.managedIdentity}${environmentName}-${regionalSuffix}'
    location: location
    tags: tags
  }
}

// =============================================================================
// VNET & PRIVATE DNS ZONES
// Must be deployed before any services that use private endpoints.
// =============================================================================

module vnet 'modules/network/virtual-network.bicep' = {
  name: 'virtual-network'
  params: {
    name: '${abbrs.virtualNetwork}${environmentName}-${regionalSuffix}'
    location: location
    tags: tags
  }
}

module privateDnsZones 'modules/network/private-dns-zones.bicep' = {
  name: 'private-dns-zones'
  params: {
    tags: tags
    virtualNetworkId: vnet.outputs.id
  }
}

//=============================================================================
// STORAGE
// Public access enabled — demo configuration.
//=============================================================================

module storage 'modules/storage/storage-account.bicep' = {
  name: 'storage-account'
  params: {
    name: '${abbrs.storageAccountName}${environmentName}${compactRegionalSuffix}'
    location: location
    tags: tags
    sku: 'Standard_LRS'
    allowPublicAccess: false
    enablePrivateEndpoint: true
    privateEndpointSubnetId: vnet.outputs.privateEndpointsSubnetId
    blobPrivateDnsZoneId: privateDnsZones.outputs.blobZoneId
    queuePrivateDnsZoneId: privateDnsZones.outputs.queueZoneId
    tablePrivateDnsZoneId: privateDnsZones.outputs.tableZoneId
    filePrivateDnsZoneId: privateDnsZones.outputs.fileZoneId
    containers: [
      'documents'
      'aisindexer'
      'generations'
      'references'
    ]
    queues: [
      generationJobsQueueName
      generationJobsPoisonQueueName
    ]
    fileShares: []
    principalId: ''
    principalType: 'ServicePrincipal'
  }
}

//=============================================================================
// AI SERVICES
// Public access enabled — demo configuration.
//=============================================================================

module aiSearch 'modules/cognitives/aisearch.bicep' = {
  name: 'ai-search'
  params: {
    name: '${abbrs.aiSearch}${environmentName}-${regionalSuffix}'
    location: location
    tags: tags
    sku: 'standard'
    publicNetworkAccess: 'Disabled'
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
    enablePrivateEndpoint: true
    privateEndpointSubnetId: vnet.outputs.privateEndpointsSubnetId
    privateDnsZoneId: privateDnsZones.outputs.searchZoneId
  }
}

// =============================================================================
// DATABASE
// Cosmos DB stores generation and reference metadata.
// =============================================================================

module cosmosDb 'modules/database/cosmosdb.bicep' = {
  name: 'cosmosdb'
  params: {
    name: '${abbrs.cosmosDbAccount}${environmentName}-${compactRegionalSuffix}'
    location: location
    tags: apiTags
    containerNames: [
      'documents'
      'references'
    ]
    partitionKeyPath: '/ownerId'
    publicNetworkAccess: 'Disabled'
    enablePrivateEndpoint: true
    privateEndpointSubnetId: vnet.outputs.privateEndpointsSubnetId
    privateDnsZoneId: privateDnsZones.outputs.cosmosDbZoneId
    managedIdentityPrincipalId: managedIdentity.outputs.principalId
    userPrincipalId: currentUserPrincipalId
  }
}

// =============================================================================
// FOUNDRY
// Public network access enabled (required for Foundry control plane & agent hosting).
// =============================================================================

module foundry 'modules/foundry/foundry.bicep' = {
  name: 'foundry'
  params: {
    name: '${abbrs.cognitiveServices}foundry-${environmentName}-${locationAbbreviation}-01-${suffix}'
    projectName: 'foundry-iq-project'
    location: location
    tags: tags
    publicNetworkAccess: 'Disabled'
    cosmosDBName: cosmosDb.outputs.cosmosDbAccountName
    azureStorageName: storage.outputs.name
    aiSearchName: aiSearch.outputs.searchServiceName
    deployConnections: deployFoundryCapabilityHost
    enablePrivateEndpoint: true
    privateEndpointSubnetId: vnet.outputs.privateEndpointsSubnetId
    cognitiveServicesPrivateDnsZoneId: privateDnsZones.outputs.cognitiveServicesZoneId
    openaiPrivateDnsZoneId: privateDnsZones.outputs.openaiZoneId
    aiServicesPrivateDnsZoneId: privateDnsZones.outputs.aiServicesZoneId
  }
}

// Ordering is strict: pre-caphost roles → capability host → post-caphost roles.
// =============================================================================

module foundryProjectPreCapHostRoles 'modules/foundry/foundry-project-precaphost-roles.bicep' = {
  name: 'foundry-project-precaphost-roles'
  params: {
    cosmosDBName: cosmosDb.outputs.cosmosDbAccountName
    projectPrincipalId: foundry.outputs.foundryProjectPrincipalId
  }
}

module foundryCapabilityHost 'modules/foundry/foundry-capability-host.bicep' = {
  name: 'foundry-capability-host'
  params: {
    accountName: foundry.outputs.foundryName
    projectName: foundry.outputs.foundryProjectName
    cosmosDBConnection: foundry.outputs.cosmosDBConnection
    azureStorageConnection: foundry.outputs.azureStorageConnection
    aiSearchConnection: foundry.outputs.aiSearchConnection
    projectCapHost: 'caphostproj'
    deployCapabilityHost: deployFoundryCapabilityHost
  }
  dependsOn: [
    foundryProjectPreCapHostRoles
    roleAssignments
  ]
}

module formatProjectWorkspaceId 'modules/foundry/format-project-workspace-id.bicep' = {
  name: 'foundry-format-project-workspace-id'
  params: {
    projectWorkspaceId: foundry.outputs.foundryProjectWorkspaceId
  }
}

module foundryProjectPostCapHostRoles 'modules/foundry/foundry-project-postcaphost-roles.bicep' = {
  name: 'foundry-project-postcaphost-roles'
  params: {
    azureStorageName: storage.outputs.name
    cosmosDBName: cosmosDb.outputs.cosmosDbAccountName
    projectPrincipalId: foundry.outputs.foundryProjectPrincipalId
    projectWorkspaceId: formatProjectWorkspaceId.outputs.projectWorkspaceIdGuid
  }
  dependsOn: [
    foundryCapabilityHost
  ]
}

// =============================================================================
// FOUNDRY PROJECT CONNECTIONS
// =============================================================================

module foundryProjectConnections 'modules/foundry/foundry-project-connections.bicep' = {
  name: 'foundry-project-connections'
  params: {
    foundryAccountName: foundry.outputs.foundryName
    projectName: foundry.outputs.foundryProjectName
    applicationInsightsResourceId: appInsights.outputs.id
    applicationInsightsName: appInsights.outputs.name
    applicationInsightsConnectionString: appInsights.outputs.connectionString
  }
}

// =============================================================================
// CONTAINER REGISTRY — hosts the Shared Web APIs container image built by azd.
// Public access enabled — demo configuration.
// =============================================================================

module containerRegistry 'modules/hosting/container-registry.bicep' = {
  name: 'container-registry'
  params: {
    name: '${abbrs.containerRegistry}${locationAbbreviation}${uniqueString(resourceGroup().id, environmentName, normalizedLocation)}'
    location: location
    tags: tags
    sku: 'Premium'
    publicNetworkAccess: 'Enabled'
    enablePrivateEndpoint: true
    privateEndpointSubnetId: vnet.outputs.privateEndpointsSubnetId
    privateDnsZoneId: privateDnsZones.outputs.acrZoneId
  }
}


// =============================================================================
// CONTAINER APP ENVIRONMENT (public/external)
// Single Container Apps environment for all workloads (jobs + MCP server).
// Uses an external load balancer so apps are reachable via a public domain name
// (containerAppEnv.outputs.defaultDomain), while still being VNet-integrated so
// they can privately reach Foundry, Cosmos DB, Storage and AI Search.
// =============================================================================

module containerAppEnv 'modules/hosting/container-app-environment.bicep' = {
  name: 'container-app-env'
  params: {
    name: '${abbrs.containerAppEnvironment}${environmentName}-${regionalSuffix}'
    location: location
    tags: tags
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
    enableVNetIntegration: true
    infrastructureSubnetId: vnet.outputs.containerSubnetId
    internalLoadBalancerEnabled: false
  }
  dependsOn: [
    roleAssignments
  ]
}

module entraApiRegistration 'modules/security/entra-api-registration.bicep' = {
  name: 'comics-entra-api'
  params: {
    displayName: entraApiDisplayName
    identifierUri: entraApiAudience
    spaRedirectUris: [
      'http://localhost:5173'
      'http://localhost:5173/authentication/login-callback'
      'https://localhost:7148/authentication/login-callback'
    ]
  }
}

module staticWebApp 'br/public:avm/res/web/static-site:0.9.5' = {
  name: 'comics-web'
  params: {
    name: take('${abbrs.staticWebApp}${environmentName}-${staticWebAppLocationAbbreviation}-${suffix}', 40)
    location: staticWebAppLocation
    sku: 'Free'
    provider: 'None'
    publicNetworkAccess: 'Enabled'
    stagingEnvironmentPolicy: 'Disabled'
    tags: webTags
    enableTelemetry: false
  }
}

module entraApiRegistrationUpdate 'modules/security/entra-api-registration-update.bicep' = {
  name: 'comics-entra-api-update'
  params: {
    displayName: entraApiDisplayName
    uniqueName: entraApiRegistration.outputs.uniqueName
    identifierUri: entraApiAudience
    clientId: entraApiRegistration.outputs.clientId
    spaRedirectUris: [
      'https://${staticWebApp.outputs.defaultHostname}'
      'https://${staticWebApp.outputs.defaultHostname}/authentication/login-callback'
      'http://localhost:5173'
      'http://localhost:5173/authentication/login-callback'
      'https://localhost:7148/authentication/login-callback'
    ]
  }
}

module apiContainerApp 'modules/hosting/container-app.bicep' = {
  name: 'comics-api'
  params: {
    name: '${abbrs.containerApp}api-${environmentName}-${regionalSuffix}'
    location: location
    tags: apiTags
    environmentId: containerAppEnv.outputs.id
    managedIdentityId: managedIdentity.outputs.id
    containerRegistryServer: containerRegistry.outputs.loginServer
    managedIdentityClientId: managedIdentity.outputs.clientId
    containerImage: apiContainerImage
    targetPort: 8080
    externalIngress: true
    //allowedOrigins: [
    //  'https://${staticWebApp.outputs.defaultHostname}'
    //]
    appInsightsConnectionString: appInsights.outputs.connectionString
    cosmosDbEndpoint: cosmosDb.outputs.cosmosDbEndpoint
    cosmosDbDatabaseName: cosmosDb.outputs.databaseName
    storageAccountBlobEndpoint: storage.outputs.blobEndpoint
    storageAccountQueueEndpoint: storage.outputs.queueEndpoint
    jobsQueueName: generationJobsQueueName
    azureFoundryEndpoint: foundry.outputs.foundryEndpoint
    azureAISearchEndpoint: aiSearch.outputs.searchServiceEndpoint
    azureContentUnderstandingEndpoint: 'https://${foundry.outputs.foundryName}.services.ai.azure.com'
    minReplicas: 0
    maxReplicas: 3
    serviceName: 'api'
    backendAppRegistrationClientId: entraApiRegistration.outputs.clientId
    //backendAppRegistrationAudience: entraApiRegistration.outputs.audience
  }
  dependsOn: [
    roleAssignments
  ]
}

module boardGenerationJob 'modules/hosting/container-app-job.bicep' = {
  name: 'comics-generation-job'
  params: {
    name: '${abbrs.containerApp}generation-${environmentName}-${regionalSuffix}'
    location: location
    tags: apiTags
    environmentId: containerAppEnv.outputs.id
    managedIdentityId: managedIdentity.outputs.id
    managedIdentityClientId: managedIdentity.outputs.clientId
    containerRegistryServer: containerRegistry.outputs.loginServer
    containerImage: generationJobContainerImage
    appInsightsConnectionString: appInsights.outputs.connectionString
    cosmosDbEndpoint: cosmosDb.outputs.cosmosDbEndpoint
    cosmosDbDatabaseName: cosmosDb.outputs.databaseName
    storageBlobEndpoint: storage.outputs.blobEndpoint
    storageQueueEndpoint: storage.outputs.queueEndpoint
    storageAccountName: storage.outputs.name
    jobsQueueName: generationJobsQueueName
    poisonQueueName: generationJobsPoisonQueueName
    foundryEndpoint: foundry.outputs.foundryEndpoint
    serviceName: 'generationjob'
  }
  dependsOn: [
    roleAssignments
  ]
}

// =============================================================================
// RBAC ROLE ASSIGNMENTS
// =============================================================================

module roleAssignments 'modules/security/role-assignments.bicep' = {
  name: 'role-assignments'
  params: {
    principalId: managedIdentity.outputs.principalId
    userPrincipalId: currentUserPrincipalId
    foundryProjectPrincipalId: foundry.outputs.foundryProjectPrincipalId
    foundryAccountPrincipalId: foundry.outputs.foundryPrincipalId
    cosmosDbAccountName: cosmosDb.outputs.cosmosDbAccountName
    foundryName: foundry.outputs.foundryName
    storageAccountName: storage.outputs.name
    aiSearchIdentityPrincipalId: aiSearch.outputs.searchServicePrincipalId
    aiSearchName: aiSearch.outputs.searchServiceName
    containerRegistryName: containerRegistry.outputs.name
  }
}

// =============================================================================
// OUTPUTS
// =============================================================================

output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_RESOURCE_GROUP string = resourceGroup().name

// Storage
output AZURE_STORAGE_ACCOUNT_NAME string = storage.outputs.name
output AZURE_STORAGE_BLOB_ENDPOINT string = storage.outputs.blobEndpoint

// AI Services
output AZURE_SEARCH_ENDPOINT string = aiSearch.outputs.searchServiceEndpoint
output AZURE_SEARCH_NAME string = aiSearch.outputs.searchServiceName
output AZURE_SEARCH_INDEX_NAME string = 'kb-advanced-index'
output AZURE_CONTENT_UNDERSTANDING_ENDPOINT string = 'https://${foundry.outputs.foundryName}.services.ai.azure.com'
output AZURE_CONTENT_UNDERSTANDING_NAME string = foundry.outputs.foundryName

// Database
output AZURE_COSMOS_ENDPOINT string = cosmosDb.outputs.cosmosDbEndpoint
output AZURE_COSMOS_DATABASE_NAME string = cosmosDb.outputs.databaseName

// Foundry
output AZURE_FOUNDRY_ENDPOINT string = foundry.outputs.foundryEndpoint
output AZURE_FOUNDRY_PROJECT_ENDPOINT string = foundry.outputs.foundryProjectEndpoint
output AZURE_FOUNDRY_NAME string = foundry.outputs.foundryName

// Monitoring
@secure()
output APPLICATIONINSIGHTS_CONNECTION_STRING string = appInsights.outputs.connectionString
output APPLICATIONINSIGHTS_RESOURCE_ID string = appInsights.outputs.id

// AI Project
output AZURE_AI_ACCOUNT_NAME string = foundry.outputs.foundryName
output AZURE_AI_PROJECT_ENDPOINT string = foundry.outputs.foundryProjectEndpoint
output AZURE_AI_PROJECT_ID string = foundry.outputs.foundryProjectId
output AZURE_AI_PROJECT_NAME string = foundry.outputs.foundryProjectName

// Container Registry (consumed by azd for remote container build/push)
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.outputs.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = containerRegistry.outputs.name

// Container Apps Environment (public FQDN suffix for container apps hosted in this environment)
output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = containerAppEnv.outputs.defaultDomain
output SERVICE_API_URI string = 'https://${apiContainerApp.outputs.fqdn}'
output SERVICE_WEB_URI string = 'https://${staticWebApp.outputs.defaultHostname}'
output AZURE_ENTRA_CLIENT_ID string = entraApiRegistration.outputs.clientId
output AZURE_ENTRA_API_AUDIENCE string = entraApiRegistration.outputs.audience
output AZURE_ENTRA_API_SCOPE string = entraApiRegistration.outputs.delegatedScope
output VITE_ENTRA_CLIENT_ID string = entraApiRegistration.outputs.clientId
output VITE_ENTRA_TENANT_ID string = tenant().tenantId
output VITE_API_SCOPE string = entraApiRegistration.outputs.delegatedScope
output VITE_API_BASE_URL string = 'https://${apiContainerApp.outputs.fqdn}'
