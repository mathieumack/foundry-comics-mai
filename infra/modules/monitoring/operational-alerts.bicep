// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Base name for monitoring resources')
param name string

@description('Azure region')
param location string

@description('Tags to apply to resources')
param tags object = {}

@description('Application Insights resource ID queried by alert rules')
param applicationInsightsResourceId string

@description('Optional operations email address for alert notifications')
param notificationEmail string = ''

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${name}-actions'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'comics-sre'
    enabled: true
    emailReceivers: !empty(notificationEmail)
      ? [
          {
            name: 'operations'
            emailAddress: notificationEmail
            useCommonAlertSchema: true
          }
        ]
      : []
  }
}

resource failedRequestsAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${name}-failed-requests'
  location: location
  tags: tags
  properties: {
    displayName: 'Comics API repeated failed requests'
    description: 'Detects at least five failed API requests in ten minutes.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT10M'
    scopes: [
      applicationInsightsResourceId
    ]
    criteria: {
      allOf: [
        {
          query: 'requests | where success == false'
          timeAggregation: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 5
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

resource exceptionsAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${name}-exceptions'
  location: location
  tags: tags
  properties: {
    displayName: 'Comics application exceptions'
    description: 'Detects application exceptions in the last ten minutes.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT10M'
    scopes: [
      applicationInsightsResourceId
    ]
    criteria: {
      allOf: [
        {
          query: 'exceptions'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

output actionGroupId string = actionGroup.id
