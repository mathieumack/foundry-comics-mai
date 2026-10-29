@description('Service Bus namespace name')
param name string

@description('Azure region')
param location string

@description('Tags to apply to resources')
param tags object = {}

@description('Queue names to create')
param queueNames array

@description('Subnet ID for the private endpoint')
param privateEndpointSubnetId string

@description('Service Bus private DNS zone ID')
param privateDnsZoneId string

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Premium'
    tier: 'Premium'
    capacity: 1
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    zoneRedundant: false
  }
}

resource queues 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = [for queueName in queueNames: {
  parent: serviceBusNamespace
  name: queueName
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 5
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    enablePartitioning: false
  }
}]

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = {
  name: '${name}-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-connection'
        properties: {
          privateLinkServiceId: serviceBusNamespace.id
          groupIds: [
            'namespace'
          ]
        }
      }
    ]
  }
}

resource privateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'servicebus'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output id string = serviceBusNamespace.id
output name string = serviceBusNamespace.name
output fullyQualifiedNamespace string = '${serviceBusNamespace.name}.servicebus.windows.net'
