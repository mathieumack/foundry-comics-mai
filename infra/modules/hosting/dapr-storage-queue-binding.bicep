@description('Container Apps environment name')
param environmentName string

@description('Dapr binding component and Storage Queue name')
param name string

@description('Storage account name')
param storageAccountName string

@description('User-assigned managed identity client ID')
param managedIdentityClientId string

@description('Dapr app IDs allowed to use the component')
param scopes array

resource binding 'Microsoft.App/managedEnvironments/daprComponents@2025-07-01' = {
  name: '${environmentName}/${name}'
  properties: {
    componentType: 'bindings.azure.storagequeues'
    version: 'v1'
    ignoreErrors: false
    initTimeout: '30s'
    metadata: [
      {
        name: 'accountName'
        value: storageAccountName
      }
      {
        name: 'queueName'
        value: name
      }
      {
        name: 'azureClientId'
        value: managedIdentityClientId
      }
      {
        name: 'direction'
        value: 'input'
      }
    ]
    scopes: scopes
  }
}
