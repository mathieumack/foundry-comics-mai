@description('Name of the Container Apps Job')
param name string

@description('Azure region')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('Container Apps Environment resource ID')
param environmentId string

@description('Container image deployed by azd')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:dotnetapp'

@description('User-assigned managed identity resource ID')
param managedIdentityId string

@description('User-assigned managed identity client ID')
param managedIdentityClientId string

@description('Container registry login server')
param containerRegistryServer string

@description('Application Insights connection string')
param appInsightsConnectionString string

@description('Cosmos DB endpoint')
param cosmosDbEndpoint string

@description('Cosmos DB database name')
param cosmosDbDatabaseName string

@description('Storage Blob service endpoint')
param storageBlobEndpoint string

@description('Storage Queue service endpoint')
param storageQueueEndpoint string

@description('Storage account name used by the Queue scaler')
param storageAccountName string

@description('Queue that triggers board generation jobs')
param jobsQueueName string

@description('Queue that receives messages after the maximum dequeue count')
param poisonQueueName string

@description('Microsoft Foundry account endpoint')
param foundryEndpoint string

@description('azd service name')
param serviceName string = 'generationjob'

resource job 'Microsoft.App/jobs@2025-07-01' = {
  name: name
  location: location
  tags: union(tags, {
    'azd-service-name': serviceName
  })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    configuration: {
      triggerType: 'Event'
      replicaTimeout: 21600
      replicaRetryLimit: 0
      eventTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
        scale: {
          minExecutions: 0
          maxExecutions: 1
          pollingInterval: 30
          rules: [
            {
              name: 'generation-queue'
              type: 'azure-queue'
              metadata: {
                accountName: storageAccountName
                queueName: jobsQueueName
                queueLength: '1'
              }
              identity: managedIdentityId
            }
          ]
        }
      }
      registries: [
        {
          server: containerRegistryServer
          identity: managedIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: name
          image: containerImage
          env: [
            {
              name: 'DOTNET_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: managedIdentityClientId
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
              name: 'StorageAccount__BlobsEndpoint'
              value: storageBlobEndpoint
            }
            {
              name: 'StorageAccount__QueuesEndpoint'
              value: storageQueueEndpoint
            }
            {
              name: 'Storage__JobsQueueName'
              value: jobsQueueName
            }
            {
              name: 'Storage__PoisonQueueName'
              value: poisonQueueName
            }
            {
              name: 'Foundry__Endpoint'
              value: foundryEndpoint
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
          ]
          resources: {
            cpu: json('2.0')
            memory: '4Gi'
          }
        }
      ]
    }
    workloadProfileName: 'Consumption'
  }
}

output id string = job.id
output name string = job.name
