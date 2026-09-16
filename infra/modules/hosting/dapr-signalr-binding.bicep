@description('Container Apps environment name')
param environmentName string

@description('Azure SignalR Service endpoint')
param signalREndpoint string

@description('User-assigned managed identity client ID')
param managedIdentityClientId string

@description('Dapr app IDs allowed to use the component')
param scopes array

resource binding 'Microsoft.App/managedEnvironments/daprComponents@2025-07-01' = {
  name: '${environmentName}/signalr'
  properties: {
    componentType: 'bindings.azure.signalr'
    version: 'v1'
    ignoreErrors: false
    initTimeout: '30s'
    metadata: [
      {
        name: 'endpoint'
        value: signalREndpoint
      }
      {
        name: 'azureClientId'
        value: managedIdentityClientId
      }
      {
        name: 'hub'
        value: 'notificationhub'
      }
    ]
    scopes: scopes
  }
}
