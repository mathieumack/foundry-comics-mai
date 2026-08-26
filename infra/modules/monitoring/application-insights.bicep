// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Name of the Application Insights resource')
param name string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('Log Analytics workspace ID')
param workspaceResourceId string

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspaceResourceId
  }
}

output id string = appInsights.id
output name string = appInsights.name
@secure()
output instrumentationKey string = appInsights.properties.InstrumentationKey
@secure()
output connectionString string = appInsights.properties.ConnectionString
