# Requirements

This environment is intended solely for training and discovery of Azure services and MAI models. Read the [educational disclaimer and transparency FAQ](responsible-ai.md) before provisioning.

## Local tools

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerShell 7](https://learn.microsoft.com/powershell/)
- [Docker](https://docs.docker.com/get-docker/)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
- [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- Git

`azd` uses remote container builds, but Docker remains useful for local container validation.

GitHub Actions setup additionally requires [GitHub CLI](https://cli.github.com/) locally. The workflow installs .NET 10 and azd, and signs both azd and Azure CLI in with OIDC. Parameter mappings for pipeline configuration are maintained in `infra/main.parameters.json` alongside `infra/main.bicepparam`; see [deployment](deployment.md#configure-github-actions).

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

The default primary location is `swedencentral`. Static Web Apps is explicitly provisioned in `westeurope`, not in the primary location. Before provisioning, confirm that the target subscription and region support:

- `MAI-Thinking-1`, version `2026-06-01`, with `GlobalStandard` capacity 500.
- `MAI-Image-2.5-Pro`, version `2026-06-19`, with `GlobalStandard` capacity 2.
- Azure AI Search Standard, Azure SignalR Service Standard, Cosmos DB, Container Apps, Container Registry Premium, and the required private endpoint types.

Catalog availability does not reserve capacity. Confirm model quota separately in Microsoft Foundry or through your Azure quota process. If a model version or capacity is unavailable, update and validate the Bicep model definitions before provisioning.

## Deployment principal

`DEPLOYER_PRINCIPAL_ID` is required because Bicep grants the deploying user data-plane access to provisioned resources. For an interactive Azure CLI user:

```powershell
$principalId = az ad signed-in-user show --query id --output tsv
azd env set DEPLOYER_PRINCIPAL_ID $principalId
```

For automation, use the object ID of the deployment service principal or workload identity and ensure it has equivalent deployment permissions.

## Cost and production readiness

The environment creates billable AI model deployments, AI Search Standard, SignalR Service Standard, Container Registry Premium, networking, storage, Cosmos DB, monitoring, and two event-driven Container Apps Jobs: one for board generation and one for book-plan generation. Review current Azure pricing and quotas before deployment.

Create a budget and cost alerts, use non-sensitive training inputs, and plan environment teardown. Event-driven jobs can scale to zero, but that does not make the complete environment free while idle. AI Search and the Foundry capability host are provisioned even though current generation flows do not use search retrieval or hosted-agent orchestration.

The repository is not intended for production use. Any independent adaptation would need its own assessment of content safety, private ingress, CORS, availability, backup and retention, alerting, rate limits, image moderation, and lifecycle policies. MIT grants no warranty or production-readiness guarantee.