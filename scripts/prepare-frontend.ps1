# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

$ErrorActionPreference = 'Stop'

$requiredVariables = @(
    'SERVICE_API_URI',
    'AZURE_TENANT_ID',
    'AZURE_ENTRA_CLIENT_ID',
    'AZURE_ENTRA_API_SCOPE'
)

foreach ($variable in $requiredVariables) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($variable))) {
        throw "Environment variable $variable is required to package the frontend. Run azd provision first."
    }
}

$configuration = [ordered]@{
    ApiBaseUrl = "$($env:SERVICE_API_URI.TrimEnd('/'))/"
    AzureAd = [ordered]@{
        Authority = "https://login.microsoftonline.com/$env:AZURE_TENANT_ID"
        ClientId = $env:AZURE_ENTRA_CLIENT_ID
        ValidateAuthority = $true
        DefaultAccessTokenScopes = @($env:AZURE_ENTRA_API_SCOPE)
    }
}

$targetPath = Join-Path $PSScriptRoot '..\src\MaiImageGenerator.Web\wwwroot\appsettings.Production.json'
$configuration | ConvertTo-Json -Depth 4 | Set-Content -Path $targetPath -Encoding utf8
Write-Host "Prepared frontend configuration at $targetPath"