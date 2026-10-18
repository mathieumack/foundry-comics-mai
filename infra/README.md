# Infrastructure

[`main.bicep`](main.bicep) is the active resource-group-scoped deployment entry point selected by the root `azure.yaml`. [`main.bicepparam`](main.bicepparam) maps the `azd` environment to its parameters.

The deployment creates monitoring and alerts, Azure SRE Agent with dedicated read-only identities, the application user-assigned identity, virtual networking and private DNS, Storage, AI Search, Cosmos DB, Azure SignalR Service, Microsoft Foundry and its project capability host, Container Registry, the Container Apps API, two separate Container Apps Jobs (board generation and book planning), Static Web Apps, an Entra application, and the required role assignments.

Do not call modules directly for normal deployments. Use the root `azd` workflow described in [the deployment guide](../docs/deployment.md). See [the architecture guide](../docs/architecture.md) for resource relationships and security boundaries.

Validate the active graph with:

```powershell
az bicep build --file .\infra\main.bicep
azd show --output json
```