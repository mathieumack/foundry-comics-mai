@description('Container Apps environment name')
param environmentName string

@description('Dapr binding component and Service Bus queue name')
param name string

@description('Fully qualified Service Bus namespace')
param fullyQualifiedNamespace string

@description('User-assigned managed identity client ID')
param managedIdentityClientId string

@description('Dapr app IDs allowed to use the component')
param scopes array

resource binding 'Microsoft.App/managedEnvironments/daprComponents@2025-07-01' = {
  name: '${environmentName}/${name}'
  properties: {
    componentType: 'bindings.azure.servicebusqueues'
    version: 'v1'
    ignoreErrors: false
    initTimeout: '30s'
    metadata: [
      {
        name: 'namespaceName'
        value: fullyQualifiedNamespace
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
        name: 'disableEntityManagement'
        value: 'true'
      }
      {
        name: 'direction'
        value: 'input'
      }
    ]
    scopes: scopes
  }
}
