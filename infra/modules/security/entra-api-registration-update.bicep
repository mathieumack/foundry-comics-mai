// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.

extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0'

@description('Display name of the existing Microsoft Entra application')
param displayName string

@description('Immutable unique name of the existing Microsoft Entra application')
param uniqueName string

@description('Stable identifier URI used as the API token audience')
param identifierUri string

@description('Client ID of this application, used to request its own delegated API scope')
param clientId string

@description('Redirect URIs allowed for the browser-based single-page application')
param spaRedirectUris string[]

var accessAsUserScopeId = guid(identifierUri, 'access_as_user')

resource application 'Microsoft.Graph/applications@v1.0' = {
  displayName: displayName
  uniqueName: uniqueName
  signInAudience: 'AzureADMyOrg'
  identifierUris: [
    identifierUri
  ]
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: accessAsUserScopeId
        value: 'access_as_user'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'Access MAI Comic Generator API'
        adminConsentDescription: 'Allows the application to access the MAI Comic Generator API on behalf of the signed-in user.'
        userConsentDisplayName: 'Access MAI Comic Generator API'
        userConsentDescription: 'Allows this application to access the MAI Comic Generator API on your behalf.'
      }
    ]
  }
  spa: {
    redirectUris: spaRedirectUris
  }
  requiredResourceAccess: [
    {
      resourceAppId: '00000003-0000-0000-c000-000000000000'
      resourceAccess: [
        {
          id: 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'
          type: 'Scope'
        }
      ]
    }
    {
      resourceAppId: clientId
      resourceAccess: [
        {
          id: accessAsUserScopeId
          type: 'Scope'
        }
      ]
    }
  ]
}
