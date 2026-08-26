---
name: azd-deployment
description: "Prepare, review, validate, or troubleshoot MAI Comic Studio Azure deployments using azd, Bicep, Container Apps, Static Web Apps, Foundry, MAI models, Entra ID, managed identity, RBAC, private endpoints, quotas, and lifecycle hooks. Use for azure.yaml, infra, deployment, provisioning, model capacity, or Azure configuration work."
---

# azd Deployment

## Sources of truth

1. Read [`docs/requirements.md`](../../../docs/requirements.md) and [`docs/deployment.md`](../../../docs/deployment.md).
2. Use `azure.yaml` for services and hooks, `infra/main.bicep` for the resource graph, and `infra/main.bicepparam` for `azd` parameter mapping.
3. Change an active module only when it is reached from `infra/main.bicep`.

## Rules

- Do not run a cloud deployment or destructive command without explicit user approval.
- Preserve managed identity, RBAC, private endpoint, and private DNS patterns.
- Keep sensitive outputs secure and never copy `azd` environment values into checked-in appsettings.
- Preserve the capability-host preprovision hook; Foundry connections become immutable after host creation.
- Verify `DEPLOYER_PRINCIPAL_ID`, target subscription, region, MAI model versions, and quota before provisioning.
- Treat model names, versions, capacities, RAI policy, public access, and SKUs as reviewed architecture decisions.
- Update requirements, deployment, and architecture documentation with infrastructure behavior changes.

## Validation

Run locally before proposing deployment:

```powershell
az bicep build --file .\infra\main.bicep
azd show --output json
dotnet build .\src\MaiImageGenerator.slnx
```

Use Azure preflight or what-if validation when credentials and subscription context are available. Distinguish local compilation from live quota and permission validation.