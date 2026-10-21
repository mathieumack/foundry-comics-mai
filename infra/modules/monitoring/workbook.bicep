// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
targetScope = 'resourceGroup'

@description('Stable seed used to generate the workbook resource GUID')
param name string

@description('Workbook display name')
param displayName string

@description('Location for the resource')
param location string

@description('Tags to apply to the resource')
param tags object = {}

@description('Application Insights component resource ID')
param applicationInsightsResourceId string

var workbookContent = {
  '$schema': 'https://github.com/Microsoft/Application-Insights-Workbooks/blob/master/schema/workbook.json'
  version: 'Notebook/1.0'
  defaultResourceIds: [
    applicationInsightsResourceId
  ]
  fallbackResourceIds: [
    applicationInsightsResourceId
  ]
  items: [
    {
      type: 1
      content: {
        json: '''
          # Comics operations

          Correlated generation, dependency, retry, failure, request, and runtime telemetry. Data is read from the workspace-backed Application Insights resource and is subject to its ingestion and retention settings.
          '''
      }
      name: 'overview'
    }
    {
      type: 9
      content: {
        version: 'KqlParameterItem/1.0'
        parameters: [
          {
            id: 'd4f93eb8-7b35-4a97-92ac-f75531ba7880'
            version: 'KqlParameterItem/1.0'
            name: 'TimeRange'
            type: 4
            isRequired: true
            value: {
              durationMs: 86400000
            }
            typeSettings: {
              selectableValues: [
                {
                  durationMs: 3600000
                  displayName: 'Last hour'
                }
                {
                  durationMs: 14400000
                  displayName: 'Last 4 hours'
                }
                {
                  durationMs: 43200000
                  displayName: 'Last 12 hours'
                }
                {
                  durationMs: 86400000
                  displayName: 'Last 24 hours'
                }
                {
                  durationMs: 604800000
                  displayName: 'Last 7 days'
                }
                {
                  durationMs: 2592000000
                  displayName: 'Last 30 days'
                }
              ]
            }
          }
        ]
        style: 'pills'
        queryType: 0
        resourceType: 'microsoft.insights/components'
      }
      name: 'time-range'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Operation outcomes over time'
        query: '''
          traces
          | extend OperationName = tostring(customDimensions.OperationName),
                   Outcome = tostring(customDimensions.Outcome)
          | where isnotempty(OperationName) and isnotempty(Outcome)
          | extend Series = strcat(OperationName, ' / ', Outcome)
          | summarize Count = count() by bin(timestamp, 15m), Series
          | order by timestamp asc
          '''
        size: 0
        timeContext: {
          durationMs: 86400000
        }
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'timechart'
      }
      name: 'operation-outcomes'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Operation summary and latency'
        query: '''
          traces
          | extend OperationName = tostring(customDimensions.OperationName),
                   Outcome = tostring(customDimensions.Outcome),
                   ElapsedMs = todouble(customDimensions.ElapsedMs)
          | where isnotempty(OperationName)
          | where isnotempty(Outcome) or severityLevel >= 2
          | summarize Events = count(),
                      Succeeded = countif(Outcome in ('Succeeded', 'AlreadyCompleted', 'Reused', 'ObsoleteOrCompleted')),
                      NeedsAttention = countif(severityLevel >= 2 or Outcome in ('TimedOut', 'Canceled', 'RetryScheduled', 'TerminalFailure', 'ContentSafetyRejected')),
                      P95Ms = round(percentile(ElapsedMs, 95), 0)
            by cloud_RoleName, OperationName
          | order by NeedsAttention desc, P95Ms desc
          '''
        size: 0
        timeContext: {
          durationMs: 86400000
        }
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
      name: 'operation-summary'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Recent warnings, retries, and failures'
        query: '''
          traces
          | extend OperationName = tostring(customDimensions.OperationName),
                   Outcome = tostring(customDimensions.Outcome),
                   GenerationStage = tostring(customDimensions.GenerationStage),
                   SafeErrorCode = tostring(customDimensions.SafeErrorCode),
                   Attempt = toint(customDimensions.Attempt)
          | where isnotempty(OperationName)
          | where severityLevel >= 2
              or Outcome in ('TimedOut', 'Canceled', 'RetryScheduled', 'TerminalFailure', 'ContentSafetyRejected')
          | project timestamp,
                    cloud_RoleName,
                    severityLevel,
                    OperationName,
                    Outcome,
                    GenerationStage,
                    SafeErrorCode,
                    Attempt,
                    operation_Id
          | top 100 by timestamp desc
          '''
        size: 0
        timeContext: {
          durationMs: 86400000
        }
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
      name: 'recent-failures'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Dependency and provider health'
        query: '''
          traces
          | extend DependencyType = tostring(customDimensions.DependencyType),
                   Provider = tostring(customDimensions.Provider),
                   Model = tostring(customDimensions.Model),
                   Outcome = tostring(customDimensions.Outcome),
                   UpstreamStatus = tostring(customDimensions.UpstreamStatus),
                   ElapsedMs = todouble(customDimensions.ElapsedMs)
          | where isnotempty(DependencyType) or isnotempty(Provider)
          | where isnotempty(Outcome)
          | summarize Events = count(),
                      NeedsAttention = countif(severityLevel >= 2 or Outcome in ('TimedOut', 'Canceled', 'RetryScheduled', 'TerminalFailure')),
                      P95Ms = round(percentile(ElapsedMs, 95), 0)
            by cloud_RoleName, DependencyType, Provider, Model, UpstreamStatus
          | order by NeedsAttention desc, P95Ms desc
          '''
        size: 0
        timeContext: {
          durationMs: 86400000
        }
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
      name: 'dependency-health'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'API request health'
        query: '''
          requests
          | summarize Requests = count(),
                      Failures = countif(success == false),
                      P95Ms = round(percentile(duration, 95) / 1ms, 0)
            by name
          | extend FailureRatePct = iff(Requests == 0, 0.0, round(100.0 * Failures / Requests, 1))
          | order by Failures desc, P95Ms desc
          '''
        size: 0
        timeContext: {
          durationMs: 86400000
        }
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
      name: 'request-health'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: '.NET runtime metrics'
        query: '''
          customMetrics
          | where name startswith 'process.runtime.dotnet'
          | summarize Samples = count(),
                      Average = round(avg(value), 2),
                      P95 = round(percentile(value, 95), 2)
            by cloud_RoleName, name
          | order by cloud_RoleName asc, name asc
          '''
        size: 0
        timeContext: {
          durationMs: 86400000
        }
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
      name: 'runtime-metrics'
    }
  ]
}

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, name)
  location: location
  kind: 'shared'
  tags: union(tags, {
    'hidden-title': displayName
  })
  properties: {
    displayName: displayName
    serializedData: string(workbookContent)
    version: '1.0'
    sourceId: applicationInsightsResourceId
    category: 'workbook'
  }
}

output id string = workbook.id
