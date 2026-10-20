# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# This solution is provided for testing, learning, and evaluation purposes only.
# It is not intended for production use. Microsoft provides no support or guarantees.
# Use at your own risk.

<#
.SYNOPSIS
    Makes 'azd provision' idempotent for the Foundry Agents capability host.

.DESCRIPTION
    The Foundry BYO connections (Cosmos DB, Storage, AI Search) become owned by the
    Agents capability host once it is created. After that, the connections can no longer
    be modified through the connections API, so re-running 'azd provision' fails with:

        Connection '<name>' is in use by the workspace capability host and cannot be
        modified or deleted through the connections API.

    This preprovision hook auto-detects whether an Agents capability host already exists
    in the target resource group and sets the DEPLOY_FOUNDRY_CAPABILITY_HOST azd
    environment variable accordingly:

        - No capability host found (first deployment)  -> true  (create connections + caphost)
        - Capability host already exists (re-provision) -> false (skip immutable resources)

    The main.bicepparam file reads this variable, so the deployment becomes idempotent
    without any manual 'azd env set' step.
#>

$ErrorActionPreference = 'Stop'

function Set-Flag([string]$value, [string]$reason) {
    azd env set DEPLOY_FOUNDRY_CAPABILITY_HOST $value | Out-Null
    Write-Host "DEPLOY_FOUNDRY_CAPABILITY_HOST = $value ($reason)"
}

# Resolve subscription and resource group from the azd environment.
$subscriptionId = (azd env get-value AZURE_SUBSCRIPTION_ID 2>$null)
$resourceGroup = (azd env get-value AZURE_RESOURCE_GROUP 2>$null)
$envName = (azd env get-value AZURE_ENV_NAME 2>$null)

# In CI (e.g. GitHub Actions) the azd environment is created from scratch on every run, so these
# values only exist as process environment variables. Fall back to them, then to the Azure CLI context.
if ([string]::IsNullOrWhiteSpace($subscriptionId)) { $subscriptionId = $env:AZURE_SUBSCRIPTION_ID }
if ([string]::IsNullOrWhiteSpace($subscriptionId)) { $subscriptionId = (az account show --query id -o tsv 2>$null) }
if ([string]::IsNullOrWhiteSpace($resourceGroup)) { $resourceGroup = $env:AZURE_RESOURCE_GROUP }
if ([string]::IsNullOrWhiteSpace($envName)) { $envName = $env:AZURE_ENV_NAME }

if ([string]::IsNullOrWhiteSpace($resourceGroup) -and -not [string]::IsNullOrWhiteSpace($envName)) {
    # azd default resource group naming convention.
    $resourceGroup = "rg-$envName"
}

if ([string]::IsNullOrWhiteSpace($subscriptionId) -or [string]::IsNullOrWhiteSpace($resourceGroup)) {
    Set-Flag 'true' 'subscription or resource group not resolved yet — assuming first deployment'
    return
}

# If the resource group does not exist yet, this is a first deployment.
$rgExists = (az group exists --name $resourceGroup --subscription $subscriptionId 2>$null)
if ($rgExists -ne 'true') {
    Set-Flag 'true' "resource group '$resourceGroup' does not exist yet — first deployment"
    return
}

# Look for an existing Agents capability host on any Foundry (AIServices) project in the RG.
$caphostExists = $false
try {
    $accounts = az cognitiveservices account list `
        --resource-group $resourceGroup `
        --subscription $subscriptionId `
        --query "[?kind=='AIServices'].name" -o tsv 2>$null

    foreach ($account in ($accounts -split "`n" | Where-Object { $_ -and $_.Trim() })) {
        $account = $account.Trim()

        $projectsUrl = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.CognitiveServices/accounts/$account/projects?api-version=2025-06-01"
        $projectsJson = az rest --method get --url $projectsUrl 2>$null
        if ([string]::IsNullOrWhiteSpace($projectsJson)) { continue }

        $projects = ($projectsJson | ConvertFrom-Json).value
        foreach ($project in $projects) {
            # The projects API returns parent-qualified names (e.g. '<account>/<project>').
            # Only the final segment is valid in the capabilityHosts URL.
            $projectName = ($project.name -split '/')[-1]
            $caphostUrl = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.CognitiveServices/accounts/$account/projects/$projectName/capabilityHosts?api-version=2026-03-15-preview"
            $caphostJson = az rest --method get --url $caphostUrl 2>$null
            if ([string]::IsNullOrWhiteSpace($caphostJson)) { continue }

            $caphosts = ($caphostJson | ConvertFrom-Json).value
            if ($caphosts -and $caphosts.Count -gt 0) {
                $caphostExists = $true
                Write-Host "Found existing capability host on $account/$projectName."
                break
            }
        }
        if ($caphostExists) { break }
    }
}
catch {
    # If detection fails for any reason, fall back to the safe first-deployment behaviour.
    Set-Flag 'true' "capability host detection failed ($($_.Exception.Message)) — assuming first deployment"
    return
}

if ($caphostExists) {
    Set-Flag 'false' 'capability host already exists — skipping immutable connections + caphost (idempotent re-provision)'
}
else {
    Set-Flag 'true' 'no capability host found — first deployment'
}
