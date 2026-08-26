// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the Virtual Network')
param name string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('Address prefix for the VNet')
param addressPrefix string = '10.0.0.0/16'

@description('Container environment subnet name')
param containerSubnetName string = 'snet-container-env'

@description('Container environment subnet address prefix')
param containerSubnetAddressPrefix string = '10.0.0.0/23'

@description('Private endpoints subnet name')
param privateEndpointsSubnetName string = 'snet-private-endpoints'

@description('Private endpoints subnet address prefix')
param privateEndpointsSubnetAddressPrefix string = '10.0.2.0/24'

@description('Foundry agents subnet name — dedicated subnet used for Foundry networkInjections (no delegation, no conflict with Container Apps)')
param foundryAgentsSubnetName string = 'snet-foundry-agents'

@description('Foundry agents subnet address prefix')
param foundryAgentsSubnetAddressPrefix string = '10.0.4.0/24'

resource natGatewayPublicIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: '${name}-nat-pip'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource natGateway 'Microsoft.Network/natGateways@2024-05-01' = {
  name: '${name}-nat'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    idleTimeoutInMinutes: 10
    publicIpAddresses: [
      {
        id: natGatewayPublicIp.id
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        addressPrefix
      ]
    }
    subnets: [
      {
        name: containerSubnetName
        properties: {
          addressPrefix: containerSubnetAddressPrefix
          defaultOutboundAccess: false
          natGateway: {
            id: natGateway.id
          }
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: privateEndpointsSubnetName
        properties: {
          addressPrefix: privateEndpointsSubnetAddressPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
      {
        // Dedicated subnet for Foundry Agent network injection.
        name: foundryAgentsSubnetName
        properties: {
          addressPrefix: foundryAgentsSubnetAddressPrefix
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
    ]
  }
}

output id string = vnet.id
output name string = vnet.name
// Subnet outputs use fixed positional indices that match the subnets array order above.
// Ordering: [0] snet-container-env, [1] snet-private-endpoints, [2] snet-foundry-agents.
// Do not reorder subnets without updating these indices.
output containerSubnetId string = vnet.properties.subnets[0].id
output privateEndpointsSubnetId string = vnet.properties.subnets[1].id
output foundryAgentsSubnetId string = vnet.properties.subnets[2].id
