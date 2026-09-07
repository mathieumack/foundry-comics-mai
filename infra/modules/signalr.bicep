// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the Azure SignalR Service')
param name string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

resource signalR 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: name
  location: location
  tags: tags
  kind: 'SignalR'
  sku: {
    name: 'Standard_S1'
    tier: 'Standard'
    capacity: 1
  }
  properties: {
    features: [
      {
        flag: 'ServiceMode'
        value: 'Default'
      }
    ]
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

output id string = signalR.id
output name string = signalR.name
output endpoint string = 'https://${signalR.properties.hostName}'
