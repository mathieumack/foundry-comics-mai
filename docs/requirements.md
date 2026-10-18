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

You need an Azure subscription and a Microsoft Entra tenant where you can create the resources declared in `infra/main.bicep`, create role assignments, and create or update an Entra application through the Microsoft Graph Bicep extension. Azure SRE Agent additionally requires access to the preview `Microsoft.App/agents` resource type and an Azure CLI data-plane token for `https://azuresre.dev`. Organization policies may require an administrator to grant these capabilities. The deployment does not accept a client secret as a substitute.

The deploying Azure principal becomes SRE Agent Administrator on the created agent. Authenticate the data plane before provisioning:

```powershell
az login --scope https://azuresre.dev/.default
```

To connect this private repository, a GitHub administrator must create and install a dedicated GitHub App on this repository only. Grant Metadata read, Contents read, and Issues read/write; keep all other repository and organization permissions disabled. Store its private key in Azure Key Vault, never in this repository or `.azure`.

## Region and model capacity

The default primary location is `swedencentral`. Static Web Apps is explicitly provisioned in `westeurope`, not in the primary location. Before provisioning, confirm that the target subscription and region support:

- `MAI-Thinking-1`, version `2026-06-01`, with `GlobalStandard` capacity 500.
- `MAI-Image-2.5-Pro`, version `2026-06-19`, with `GlobalStandard` capacity 2.
- Azure AI Search Standard, Azure SignalR Service Standard, Cosmos DB, Container Apps, Container Registry Premium, and the required private endpoint types.
- Azure SRE Agent in the primary region. The service and its ARM/data-plane APIs are preview and regional availability can change.

Catalog availability does not reserve capacity. Confirm model quota separately in Microsoft Foundry or through your Azure quota process. If a model version or capacity is unavailable, update and validate the Bicep model definitions before provisioning.

## Cost and production readiness

The environment creates billable AI model deployments, AI Search Standard, SignalR Service Standard, Container Registry Premium, networking, storage, Cosmos DB, monitoring, and two event-driven Container Apps Jobs: one for board generation and one for book-plan generation. Review current Azure pricing and quotas before deployment.

Create a budget and cost alerts, use non-sensitive training inputs, and plan environment teardown. Event-driven jobs can scale to zero, but that does not make the complete environment free while idle. Azure SRE Agent has a fixed always-on charge plus active-flow Azure Agent Units; the default `SRE_MONTHLY_AGENT_UNIT_LIMIT=500` caps only active flow. AI Search and the Foundry capability host are provisioned even though current generation flows do not use search retrieval or hosted-agent orchestration.

Set `SRE_ALERT_EMAIL` in the ignored `azd` environment when alerts need email delivery. Without it, alerts and SRE investigations still run, but the action group has no email receiver. See [Azure SRE Agent operations](sre-agent.md) for permissions, audit, retention, GitHub setup, and cost controls.

The repository is not intended for production use. Any independent adaptation would need its own assessment of content safety, private ingress, CORS, availability, backup and retention, alerting, rate limits, image moderation, and lifecycle policies. MIT grants no warranty or production-readiness guarantee.