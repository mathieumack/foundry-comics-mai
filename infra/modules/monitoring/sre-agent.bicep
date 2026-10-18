// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the Azure SRE Agent')
param name string

@description('Azure region')
param location string

@description('Tags to apply to resources')
param tags object = {}

@description('Application Insights resource ID monitored by the agent')
param applicationInsightsResourceId string

@description('Application Insights application ID monitored by the agent')
param applicationInsightsAppId string

@description('Log Analytics workspace resource ID monitored by the agent')
param logAnalyticsWorkspaceId string

@description('User object ID granted SRE Agent Administrator')
param administratorPrincipalId string

@description('Monthly active-flow Azure Agent Unit limit')
@minValue(500)
@maxValue(1000000)
param monthlyAgentUnitLimit int = 500

var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var monitoringReaderRoleId = '43d0d8ad-25c7-4714-9337-8ba259a9fe05'
var logAnalyticsReaderRoleId = '73c42c96-874c-492b-b04d-ab87d138a893'
var sreAgentAdministratorRoleId = 'e79298df-d852-4c6d-84f9-5d13249d1e55'

resource agentIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${name}-id'
  location: location
  tags: tags
}

resource agentAppInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${name}-appi'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    Request_Source: 'SreAgent'
    WorkspaceResourceId: logAnalyticsWorkspaceId
  }
}

resource identityReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, agentIdentity.id, readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: agentIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource identityMonitoringReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, agentIdentity.id, monitoringReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringReaderRoleId)
    principalId: agentIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource identityLogAnalyticsReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, agentIdentity.id, logAnalyticsReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', logAnalyticsReaderRoleId)
    principalId: agentIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

#disable-next-line BCP081
resource agent 'Microsoft.App/agents@2025-05-01-preview' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${agentIdentity.id}': {}
    }
  }
  properties: {
    knowledgeGraphConfiguration: {
      identity: agentIdentity.id
      managedResources: [
        resourceGroup().id
      ]
    }
    actionConfiguration: {
      accessLevel: 'Low'
      identity: agentIdentity.id
      mode: 'Review'
    }
    logConfiguration: {
      applicationInsightsConfiguration: {
        appId: agentAppInsights.properties.AppId
        connectionString: agentAppInsights.properties.ConnectionString
        applicationInsightsResourceId: agentAppInsights.id
      }
    }
    incidentManagementConfiguration: {
      type: 'AzMonitor'
      connectionName: 'azmonitor'
    }
    upgradeChannel: 'Preview'
    monthlyAgentUnitLimit: monthlyAgentUnitLimit
    defaultModel: {
      provider: 'MicrosoftFoundry'
      name: 'Automatic'
    }
    experimentalSettings: {
      EnableWorkspaceTools: true
      EnableV2AgentLoop: true
    }
  }
  dependsOn: [
    identityReader
    identityMonitoringReader
    identityLogAnalyticsReader
  ]
}

resource systemIdentityReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, agent.id, readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: agent.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource systemIdentityMonitoringReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, agent.id, monitoringReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringReaderRoleId)
    principalId: agent.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource systemIdentityLogAnalyticsReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, agent.id, logAnalyticsReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', logAnalyticsReaderRoleId)
    principalId: agent.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource administrator 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(administratorPrincipalId)) {
  name: guid(agent.id, administratorPrincipalId, sreAgentAdministratorRoleId)
  scope: agent
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sreAgentAdministratorRoleId)
    principalId: administratorPrincipalId
    principalType: 'User'
  }
}

#disable-next-line BCP081
resource appInsightsConnector 'Microsoft.App/agents/connectors@2025-05-01-preview' = {
  parent: agent
  name: 'app-insights'
  properties: {
    dataConnectorType: 'AppInsights'
    dataSource: applicationInsightsResourceId
    extendedProperties: {
      armResourceId: applicationInsightsResourceId
      resource: {
        name: last(split(applicationInsightsResourceId, '/'))
      }
      appId: applicationInsightsAppId
    }
    identity: 'system'
  }
}

#disable-next-line BCP081
resource logAnalyticsConnector 'Microsoft.App/agents/connectors@2025-05-01-preview' = {
  parent: agent
  name: 'log-analytics'
  properties: {
    dataConnectorType: 'LogAnalytics'
    dataSource: logAnalyticsWorkspaceId
    extendedProperties: {
      armResourceId: logAnalyticsWorkspaceId
      resource: {
        name: last(split(logAnalyticsWorkspaceId, '/'))
      }
    }
    identity: 'system'
  }
}

#disable-next-line BCP081
resource azureMonitorConnector 'Microsoft.App/agents/connectors@2025-05-01-preview' = {
  parent: agent
  name: 'azure-monitor'
  properties: {
    dataConnectorType: 'AzureMonitor'
    dataSource: subscription().id
    extendedProperties: {
      armResourceId: subscription().id
      lookbackDays: 7
    }
    identity: 'system'
  }
}

output id string = agent.id
output name string = agent.name
