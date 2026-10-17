# Deploy with Azure Developer CLI

## 1. Authenticate

Azure Developer CLI manages the environment and deployment. Azure CLI is also required by the capability-host detection hook and to resolve the deployment principal.

```powershell
azd auth login
az login
```

Ensure both CLIs target the same tenant and subscription.

## 2. Create an environment

```powershell
azd env new
azd env set AZURE_LOCATION swedencentral
$principalId = az ad signed-in-user show --query id --output tsv
azd env set DEPLOYER_PRINCIPAL_ID $principalId
```

Choose a short environment name because it contributes to Azure resource names. `AZURE_SUBSCRIPTION_ID` is selected by `azd env new` and stored only in the ignored `.azure` directory.

## 3. Validate prerequisites

Confirm the model names, versions, capacities, regional availability, and quota listed in [requirements](requirements.md). Confirm permission to create role assignments and Entra applications, then run:

```powershell
az bicep build --file .\infra\main.bicep
azd show --output json
dotnet build .\src\MaiImageGenerator.slnx
```

For production-oriented environments, run an Azure preflight or what-if review before deployment.

## 4. Provision and deploy

```powershell
azd up
```

The command performs these operations:

1. The `preprovision` hook checks for an existing Foundry capability host and sets `DEPLOY_FOUNDRY_CAPABILITY_HOST`.
2. Bicep creates the resources, private networking, identities, application registration, and RBAC assignments, including Azure SignalR Service in Default mode.
3. `azd` remotely builds and deploys the API, board-generation job, and book-plan-generation job containers.
4. The web `prepackage` hook writes `appsettings.Production.json` from Bicep outputs and publishes the Blazor application.
5. `azd` deploys the frontend to Static Web Apps.

The checked-in production appsettings is only a neutral template. Never replace it with tenant-specific values manually.

## 5. Verify

Use `azd env get-values` locally to inspect deployment outputs. Open `SERVICE_WEB_URI` and sign in. Submit a small board and confirm that `generation-jobs` triggers only the board-generation job, panel notifications arrive, the job completes, and artifacts are downloadable. Then generate a book plan and confirm that `book-plan-jobs` triggers only the book-plan-generation job and that the page progresses from pending to completed. Verify that the browser acquires the API scope, the API accepts the token, both jobs use the managed identity, and telemetry reaches Application Insights.

Avoid sharing the complete environment output in issues because it contains deployment-specific identifiers and endpoints.

## Redeploy

Use `azd deploy` for application-only changes and `azd provision` when infrastructure changes. `azd up` performs both.

Foundry project connections become owned by the capability host after the first deployment. The preprovision hook detects an existing host and skips those immutable resources on subsequent provisions. Do not remove the hook without replacing this behavior.

## Troubleshooting

### `DEPLOYER_PRINCIPAL_ID` is empty

Resolve and set the signed-in user's object ID as shown in step 2. For CI, set the object ID of the automation identity.

### A MAI deployment is unavailable

Confirm the exact model version in the target region and subscription. Availability and quota vary independently. Change Bicep only after choosing a supported replacement and updating the architecture and requirements documentation.

### Capability-host connection is immutable

Run `azd provision` from the repository root so the preprovision hook executes. Inspect `DEPLOY_FOUNDRY_CAPABILITY_HOST` only if detection fails.

### Frontend authentication fails

Run `azd env refresh`, confirm the Entra outputs exist, and redeploy the `web` service. Do not commit generated production values.

### Private service access fails

Check Container Apps VNet integration, private endpoint state, private DNS links, and the managed identity's data-plane roles.

### Projects API returns HTTP 500

Confirm that the configured Cosmos database is `core` and contains the `projects`, `documents`, and `references` containers, each partitioned by `/ownerId`. Re-run `azd provision` after an infrastructure update if a required container is missing.

### SignalR live updates fail

Confirm that the API identity has both SignalR App Server and SignalR REST API Owner on the SignalR resource. The first role supports hub negotiation and server connections; the second permits the Dapr output binding to send owner-targeted events. Re-run `azd provision` after changing either assignment.

## Remove an environment

```powershell
azd down --purge
```

Review the resources before confirming. Entra application cleanup and soft-deleted resources may require separate tenant or service-specific handling.