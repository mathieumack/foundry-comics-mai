// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the Container Apps Environment')
param name string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('Log Analytics workspace resource ID')
param logAnalyticsWorkspaceId string

@description('VNet configuration - subnet ID for the environment')
param infrastructureSubnetId string = ''

@description('Enable VNet integration')
param enableVNetIntegration bool = false

@description('Enable internal load balancer for VNet-only access (no public IP). Requires enableVNetIntegration to be true.')
param internalLoadBalancerEnabled bool = false

resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    publicNetworkAccess: 'Enabled'
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2022-10-01').customerId
        sharedKey: listKeys(logAnalyticsWorkspaceId, '2022-10-01').primarySharedKey
      }
    }
    vnetConfiguration: enableVNetIntegration
      ? {
          infrastructureSubnetId: infrastructureSubnetId
          internal: internalLoadBalancerEnabled
        }
      : null
    zoneRedundant: false
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

output id string = containerAppEnvironment.id
output name string = containerAppEnvironment.name
output defaultDomain string = containerAppEnvironment.properties.defaultDomain
// staticIp can be entirely absent from the API response (e.g. internal-only environments), not just empty.
// Safe-dereference avoids an ARM output evaluation failure when the property is missing.
output staticIp string = containerAppEnvironment.properties.?staticIp ?? ''
