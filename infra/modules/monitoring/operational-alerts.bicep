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

@description('Public API health-check URL')
param apiHealthUrl string

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

resource apiAvailabilityTest 'Microsoft.Insights/webtests@2022-06-15' = {
  name: '${name}-api-health'
  location: location
  tags: union(tags, {
    'hidden-link:${applicationInsightsResourceId}': 'Resource'
  })
  kind: 'standard'
  properties: {
    SyntheticMonitorId: '${name}-api-health'
    Name: 'Comics API health'
    Enabled: true
    Frequency: 300
    Timeout: 30
    Kind: 'standard'
    RetryEnabled: true
    Locations: [
      {
        Id: 'emea-nl-ams-azr'
      }
    ]
    Request: {
      RequestUrl: apiHealthUrl
      HttpVerb: 'GET'
      ParseDependentRequests: false
      FollowRedirects: true
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      SSLCertRemainingLifetimeCheck: 7
    }
  }
}

resource apiAvailabilityAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${name}-api-unavailable'
  location: 'global'
  tags: tags
  properties: {
    description: 'Detects when the public API health check fails from its test location.'
    severity: 1
    enabled: true
    scopes: [
      apiAvailabilityTest.id
      applicationInsightsResourceId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'
      webTestId: apiAvailabilityTest.id
      componentId: applicationInsightsResourceId
      failedLocationCount: 1
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
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
