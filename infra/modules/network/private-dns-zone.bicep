// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the Private DNS Zone')
param name string

@description('Tags to apply to the resource')
param tags object = {}

@description('Virtual Network ID to link to the DNS zone')
param virtualNetworkId string

@description('Enable auto registration (typically false for private endpoint DNS zones)')
param enableAutoRegistration bool = false

@description('Enable deployment of the DNS zone')
param enableDeployment bool = true

resource privateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = if (enableDeployment) {
  name: name
  location: 'global'
  tags: tags
}

resource virtualNetworkLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = if (enableDeployment) {
  parent: privateDnsZone
  name: '${name}-link'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: {
      id: virtualNetworkId
    }
    registrationEnabled: enableAutoRegistration
  }
}

output id string = enableDeployment ? privateDnsZone.id : ''
output name string = enableDeployment ? privateDnsZone.name : ''
