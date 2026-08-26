# Requirements

## Local tools

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerShell 7](https://learn.microsoft.com/powershell/)
- [Docker](https://docs.docker.com/get-docker/)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
- [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- Git

`azd` uses remote container builds, but Docker remains useful for local container validation.

```powershell
dotnet --version
pwsh --version
docker version
az version
azd version
```

## Azure access

You need an Azure subscription and a Microsoft Entra tenant where you can create the resources declared in `infra/main.bicep`, create role assignments, and create or update an Entra application through the Microsoft Graph Bicep extension. Organization policies may require an administrator to grant these capabilities. The deployment does not accept a client secret as a substitute.

## Region and model capacity

The default location is `swedencentral`. Before provisioning, confirm that the target subscription and region support:

- `MAI-Thinking-1`, version `2026-06-01`, with `GlobalStandard` capacity 500.
- `MAI-Image-2.5-Pro`, version `2026-06-19`, with `GlobalStandard` capacity 2.
- Azure AI Search Standard, Cosmos DB, Container Apps, Container Registry Premium, and the required private endpoint types.

Catalog availability does not reserve capacity. Confirm model quota separately in Microsoft Foundry or through your Azure quota process. If a model version or capacity is unavailable, update and validate the Bicep model definitions before provisioning.

## Deployment principal

`DEPLOYER_PRINCIPAL_ID` is required because Bicep grants the deploying user data-plane access to provisioned resources. For an interactive Azure CLI user:

```powershell
$principalId = az ad signed-in-user show --query id --output tsv
azd env set DEPLOYER_PRINCIPAL_ID $principalId
```

For automation, use the object ID of the deployment service principal or workload identity and ensure it has equivalent deployment permissions.

## Cost and production readiness

The environment creates billable AI model deployments, AI Search Standard, Container Registry Premium, networking, storage, Cosmos DB, monitoring, and compute resources. Review current Azure pricing and quotas before deployment.

This repository is a reference implementation. A production adoption should additionally assess content safety, private ingress requirements, CORS restrictions, availability targets, backup and retention, alerting, rate limits, image moderation, and lifecycle policies.