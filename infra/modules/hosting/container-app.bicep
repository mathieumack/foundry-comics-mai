// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the Container App')
param name string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('Container Apps Environment ID')
param environmentId string

@description('Container image to deploy')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp@sha256:b8e94412a96b19ac12c78a7064af1bb0a6f584e752b9de7cc31ca4d45ead36b0'

@description('Managed identity ID for registry access')
param managedIdentityId string

@description('Container registry login server')
param containerRegistryServer string

@description('User assigned managed identity client ID')
param managedIdentityClientId string

@description('Target port for the container')
param targetPort int = 8080

@description('Enable external ingress')
param externalIngress bool = true

@description('Application Insights connection string')
param appInsightsConnectionString string

@description('CosmosDB endpoint')
param cosmosDbEndpoint string = ''

@description('CosmosDB database name')
param cosmosDbDatabaseName string = ''

@description('Storage Account blob endpoint')
param storageAccountBlobEndpoint string

@description('Fully qualified Service Bus namespace')
param serviceBusFullyQualifiedNamespace string

@description('Queue used to submit board generation jobs')
param jobsQueueName string

@description('Queue used to submit book plan generation jobs')
param bookPlanJobsQueueName string

@description('Azure Foundry endpoint')
param azureFoundryEndpoint string

@description('Maximum number of concurrent image provider requests')
@minValue(1)
@maxValue(16)
param maxParallelImages int = 3

@description('AI Search endpoint')
param azureAISearchEndpoint string

@description('Content Understanding AIServices endpoint (dedicated account in a CU-compatible region)')
param azureContentUnderstandingEndpoint string = ''

@description('Azure SignalR Service endpoint')
param signalREndpoint string

@description('Chat model deployment name used by the service for LLM calls')
param chatDeploymentName string = 'gpt-5.2'

@description('Minimum replicas')
@minValue(0)
@maxValue(30)
param minReplicas int = 2

@description('Maximum replicas')
@minValue(1)
@maxValue(30)
param maxReplicas int = 10

@description('Service name for the container app that can be used for azd with tag')
param serviceName string

@description('App Registration Client ID for authentication. If empty, authentication is disabled.')
param backendAppRegistrationClientId string = ''

@description('Tenant ID for authentication')
param tenantId string = tenant().tenantId

@description('File repository type: BlobStorage or FileShare')
param fileRepositoryType string = 'BlobStorage'

@description('AI Search knowledge base index name for Foundry IQ integration')
param azureAISearchKnowledgeBaseIndexName string = 'kb-advanced-index'

resource containerApp 'Microsoft.App/containerApps@2025-07-01' = {
  name: name
  location: location
  tags: union(tags, {
    'azd-service-name': serviceName
  })
  identity: !empty(managedIdentityId)
    ? {
        type: 'UserAssigned'
        userAssignedIdentities: {
          '${managedIdentityId}': {}
        }
      }
    : {
        type: 'None'
      }
  properties: {
    environmentId: environmentId
    configuration: {
      registries: [
        {
          server: containerRegistryServer
          identity: managedIdentityId
        }
      ]
      dapr: {
        enabled: true
        appId: serviceName  // Must be unique across Container Apps; used for service-to-service invocation
        appProtocol: 'http'
        appPort: targetPort
        enableApiLogging: true
      }
      ingress: {
        external: externalIngress
        targetPort: targetPort
        transport: 'http'
        allowInsecure: false
        corsPolicy: {
          allowedOrigins: [
            '*'
          ]
          allowedMethods: [
            '*'
          ]
          allowedHeaders: [
            '*'
          ]
          maxAge: 0
          allowCredentials: false
        }
      }
    }
    template: {
      containers: [
        {
          name: name
          image: containerImage
          env: concat(
            [
              {
                name: 'ASPNETCORE_ENVIRONMENT'
                value: 'Production'
              }
              {
                name: 'Foundry__Endpoint'
                value: azureFoundryEndpoint
              }
              {
                name: 'Foundry__PromptModel'
                value: 'MAI-Thinking-1'
              }
              {
                name: 'Foundry__ImageModel'
                value: 'MAI-Image-2.5-Pro'
              }
              {
                name: 'Foundry__ImageSize'
                value: '1024'
              }
              {
                name: 'Foundry__MaxParallelImages'
                value: string(maxParallelImages)
              }
              {
                name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
                value: appInsightsConnectionString
              }
              {
                name: 'CosmosDb__Endpoint'
                value: cosmosDbEndpoint
              }
              {
                name: 'CosmosDb__DatabaseName'
                value: cosmosDbDatabaseName
              }
              {
                name: 'AZURE_CLIENT_ID'
                value: managedIdentityClientId
              }
              {
                name: 'StorageAccount__BlobsEndpoint'
                value: storageAccountBlobEndpoint
              }
              {
                name: 'ServiceBus__FullyQualifiedNamespace'
                value: serviceBusFullyQualifiedNamespace
              }
              {
                name: 'ServiceBus__JobsQueueName'
                value: jobsQueueName
              }
              {
                name: 'ServiceBus__BookPlanJobsQueueName'
                value: bookPlanJobsQueueName
              }
              {
                name: 'AzureAISearch__Endpoint'
                value: azureAISearchEndpoint
              }
              {
                name: 'AzureAISearch__IndexName'
                value: 'kb-advanced-index'
              }
              {
                name: 'AzureAISearch__KnowledgeBaseIndexName'
                value: azureAISearchKnowledgeBaseIndexName
              }
              {
                name: 'AzureAISearch__KnowledgeBaseName'
                value: 'kb-advanced'
              }
              {
                name: 'AzureAISearch__KnowledgeSourceName'
                value: 'kb-advanced'
              }
              {
                name: 'FILE_REPOSITORY'
                value: fileRepositoryType
              }
              {
                name: 'MODEL_DEPLOYMENT_NAME'
                value: chatDeploymentName
              }
              {
                name: 'ContentUnderstanding__Endpoint'
                value: azureContentUnderstandingEndpoint
              }
              {
                name: 'SignalR__ConnectionString'
                value: 'Endpoint=${signalREndpoint};AuthType=azure.msi;ClientId=${managedIdentityClientId};Version=1.0;'
              }
            ]
          )
          resources: {
            cpu: json('2.0')
            memory: '4Gi'
          }
          volumeMounts: []
        }
      ]
      volumes: []
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-scaling'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
    workloadProfileName: 'Consumption'
  }
}

resource containerappsAuth 'Microsoft.App/containerApps/authConfigs@2025-07-01' = if (!empty(backendAppRegistrationClientId)) {
  parent: containerApp
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'Return401'
      excludedPaths: []
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenantId}/v2.0'
          clientId: backendAppRegistrationClientId
        }
        validation: {
          allowedAudiences: [
          ]
          defaultAuthorizationPolicy: {
            allowedPrincipals: {}
            allowedApplications: [
              backendAppRegistrationClientId
            ]
          }
        }
      }
    }
    login: {
      routes: {}
      preserveUrlFragmentsForLogins: false
      cookieExpiration: {}
      nonce: {}
    }
    encryptionSettings: {}
  }
}

output id string = containerApp.id
output name string = containerApp.name
output fqdn string = containerApp.properties.configuration.ingress.fqdn
output latestRevisionName string = containerApp.properties.latestRevisionName
