# Infrastructure

[`main.bicep`](main.bicep) is the active resource-group-scoped deployment entry point selected by the root `azure.yaml`. [`main.bicepparam`](main.bicepparam) maps the `azd` environment to its parameters.

[`main.parameters.json`](main.parameters.json) supplies the environment-variable mappings read by `azd pipeline config`, including in azd 1.31.1, which does not discover them from `.bicepparam`. Keep its mappings and defaults aligned with `main.bicepparam`; normal provisioning still uses `main.bicepparam`.

The deployment creates monitoring, a user-assigned managed identity, virtual networking and private DNS, Storage, AI Search, Cosmos DB, Azure SignalR Service, Microsoft Foundry and its project capability host, Container Registry, the Container Apps API, two separate Container Apps Jobs (board generation and book planning), Static Web Apps, and the required role assignments. It also manages an Entra application unless `EXISTING_ENTRA_CLIENT_ID` is supplied; reuse mode skips both Microsoft Graph modules, including redirect updates. See [authentication application management](../docs/deployment.md#choose-authentication-application-management).

Do not call modules directly for normal deployments. Use the root `azd` workflow described in [the deployment guide](../docs/deployment.md). See [the architecture guide](../docs/architecture.md) for resource relationships and security boundaries.

Validate the active graph with:

```powershell
az bicep build --file .\infra\main.bicep
azd show --output json
```