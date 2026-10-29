# Deploy with Azure Developer CLI

This workflow provisions a billable educational environment solely for discovering Azure services and MAI models. It is not a production deployment recommendation. Read [requirements](requirements.md) and the [responsible AI FAQ](responsible-ai.md) first.

## 1. Authenticate

Azure Developer CLI manages the environment and deployment. Azure CLI is also required by the capability-host detection hook and to resolve the deployment principal.

```powershell
azd auth login
az login
```

Ensure both CLIs target the same tenant and subscription.

For GitHub Actions, see [Configure GitHub Actions](#configure-github-actions) after selecting the local environment.

## 2. Create an environment

```powershell
azd env new
azd env set AZURE_LOCATION swedencentral
$principalId = az ad signed-in-user show --query id --output tsv
azd env set DEPLOYER_PRINCIPAL_ID $principalId
```

Choose a short environment name because it contributes to Azure resource names. `AZURE_SUBSCRIPTION_ID` is selected by `azd env new` and stored only in the ignored `.azure` directory.

## 3. Validate prerequisites

Confirm the model names, versions, capacities, regional availability, and quota listed in [requirements](requirements.md). Confirm permission to create role assignments and, unless reusing an existing authentication application below, Entra applications. Then run:

```powershell
az bicep build --file .\infra\main.bicep --stdout > $null
azd show --output json
dotnet build .\src\MaiImageGenerator.slnx
```

Local compilation does not validate live permissions, model availability, quota, or policies. Review a preflight or what-if result in the intended subscription before provisioning; do not treat this sample as production-ready.

### Choose authentication application management

By default, provisioning creates or updates the Entra authentication application, its service principal, API scope, and SPA redirect URIs through Microsoft Graph. Leave `EXISTING_ENTRA_CLIENT_ID` empty to keep this behavior.

To reuse an application without any Graph application lookup or modification, set its **application (client) ID**, not its object ID or the deployment identity's client ID:

```powershell
azd env set EXISTING_ENTRA_CLIENT_ID <authentication-application-client-id>
```

The existing application must be in the deployment tenant and support both the SPA and API: single-tenant sign-in, v2 access tokens, the delegated `access_as_user` scope, and the required delegated permissions/consent (Microsoft Graph `User.Read` and its own API scope, as in the managed application). Its default API identifier URI is `api://comics-<environment-name>`. If it exposes another URI, also set:

```powershell
azd env set EXISTING_ENTRA_API_AUDIENCE <existing-api-identifier-uri>
```

An administrator must configure SPA redirect URIs separately: `https://<static-web-app-hostname>` and `https://<static-web-app-hostname>/authentication/login-callback`. For local development, also include `http://localhost:5173`, `http://localhost:5173/authentication/login-callback`, and `https://localhost:7148/authentication/login-callback`. Obtain the deployed origin from `SERVICE_WEB_URI`; update redirects if that origin changes.

In reuse mode, neither Graph module runs, including redirect updates and service-principal creation. API authentication and frontend configuration still receive the selected client ID, audience, and scope through the usual outputs. `AZURE_ENTRA_CLIENT_ID` remains an **output**, not the input that selects this mode, so a previous deployment cannot silently disable application management. To return to managed mode, set `EXISTING_ENTRA_CLIENT_ID` to an empty string; only do this for an application managed by this deployment, since it does not adopt arbitrary existing registrations.

## 4. Provision and deploy

```powershell
azd up
```

The command performs these operations:

1. The `preprovision` hook checks for an existing Foundry capability host and sets `DEPLOY_FOUNDRY_CAPABILITY_HOST`.
2. Bicep creates the resources, private networking, identities, and RBAC assignments, including Azure SignalR Service in Default mode. It manages the authentication application and redirects only when no existing client ID is supplied.
3. `azd` remotely builds and deploys the API, board-generation job, and book-plan-generation job containers.
4. The web `prepackage` hook writes `appsettings.Production.json` from Bicep outputs and publishes the Blazor application.
5. `azd` deploys the frontend to Static Web Apps.

The checked-in production appsettings is only a neutral template. Never replace it with tenant-specific values manually.

## 5. Verify

Use `azd env get-values` locally to inspect deployment outputs. Open `SERVICE_WEB_URI` and sign in. Submit a small board and confirm that `generation-jobs` triggers only the board-generation job, panel notifications arrive, the job completes, and artifacts are downloadable. Then generate a book plan and confirm that `book-plan-jobs` triggers only the book-plan-generation job and that the page progresses from pending to completed. Verify that the browser acquires the API scope, the API accepts the token, both jobs use the managed identity, and telemetry reaches Application Insights.

Avoid sharing the complete environment output in issues because it contains deployment-specific identifiers and endpoints.

### Functional smoke checks

| Check | Expected result |
| --- | --- |
| Create a comic and submit a template-matching board | A saved version appears; panels and final board become accessible through the API. |
| Close and reopen the board page | The background execution continues and saved progress can be retrieved. |
| View panel progress | SignalR targets the signed-in owner, and completion refreshes stored state/artifacts. |
| Create a book and request a short plan | API returns `202`; only the book-plan job consumes it; polling restores the completed plan. |
| Save chapter edits and generate prose, then an image | The image dependency uses `/mai/v1/images/generations`; the response is `Completed`, and the owner-scoped PNG is retrievable. |
| Reopen the book | Saved plan, prose, image, and stage errors survive navigation. |
| Delete a completed comic and book from history | Confirmation is required; the card disappears only after all owner-scoped records and blob versions are removed. |
| Use a second authorized test account | The first account's project/artifact IDs do not expose its data. |

Use synthetic inputs and a small chapter/panel count to limit cost. Do not deliberately submit harmful material to exercise safety paths. These are manual smoke checks; the repository has no automated end-to-end test suite.

The storage account intentionally disables blob soft delete so confirmed project deletion is permanent. Blob versioning remains enabled for ordinary overwrites; the deletion API removes every version and snapshot under the project-owned prefixes before deleting Cosmos records. Re-provision the infrastructure before relying on this behavior in an existing environment.

## Redeploy

Use `azd deploy` for application-only changes and `azd provision` when infrastructure changes. `azd up` performs both.

Foundry project connections become owned by the capability host after the first deployment. The preprovision hook detects an existing host and skips those immutable resources on subsequent provisions. Do not remove the hook without replacing this behavior.

## Configure GitHub Actions

Install GitHub CLI and authenticate with `gh auth login`. Select the existing local environment rather than creating a second environment:

```powershell
azd env list
azd env select <environment-name>
azd pipeline config --provider github --auth-type federated -e <environment-name>
```

For a user-assigned managed identity, do not pass `--principal-id` or `--principal-name`: in azd 1.31.1 these options look up an app-registration-backed service principal, not a managed identity. Run the command above without either option. If prompted, select `Federated User Managed Identity (MSI + OIDC)`, then `Use existing User Managed Identity (MSI)`. After selection, azd stores the identity reference in the local `AZURE_PIPELINE_MSI_CLIENT_ID` setting and reuses it on subsequent runs.

Review the proposed role assignments and federated subjects. Accept the subjects detected for this repository, including any owner/repository numeric IDs; do not replace them with a legacy subject format. The workflow does not use a GitHub environment, so branch subjects must match the branch being deployed.

`azd pipeline config` reads variable mappings from `infra/main.parameters.json`; azd 1.31.1 does not discover them from `infra/main.bicepparam`. Both files are intentionally present. Keep all mappings and defaults synchronized. Normal provisioning uses `main.bicepparam`, including the capability-host flag set by the preprovision hook.

Check repository Actions variables after configuration:

| Variable | Value |
| --- | --- |
| `AZURE_ENV_NAME` | The selected local environment name. |
| `AZURE_LOCATION` | The primary region, normally `swedencentral`. |
| `AZURE_RESOURCE_GROUP` | The existing target resource group. Set explicitly when reusing an environment. |
| `AZURE_SUBSCRIPTION_ID` | The target subscription ID. |
| `AZURE_TENANT_ID` | The tenant containing the deployment identity. |
| `AZURE_CLIENT_ID` | The deployment identity's client/application ID. |
| `EXISTING_ENTRA_CLIENT_ID` | Optional repository secret containing the authentication application client ID. When set, skip both Graph modules and manage redirects outside CD. |
| `EXISTING_ENTRA_API_AUDIENCE` | Optional existing API identifier URI; defaults to `api://comics-<environment-name>`. Only used with an existing client ID. |

For example, copy the existing resource group without writing deployment identifiers into tracked files:

```powershell
$resourceGroup = azd env get-value AZURE_RESOURCE_GROUP
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resourceGroup)) {
    throw 'Select and refresh the existing azd environment before configuring its resource group.'
}
gh variable set AZURE_RESOURCE_GROUP --body $resourceGroup
gh variable list
```

The workflow passes these variables to `azd`, installs .NET 10, and authenticates both `azd` and Azure CLI using OIDC. Azure CLI authentication is required by the preprovision hook; `azd auth login` alone does not authenticate `az`. Deployments to the same environment are serialized to protect capability-host provisioning.

The workflow runs on pushes to `main` and manual dispatches, not on pull requests. The configured federated credential is branch-scoped, so dispatch the workflow from `main`. A dispatch from another branch fails Azure login with `AADSTS700213` unless that exact branch also has a federated credential. Commit and push the workflow and infrastructure mapping only when ready: a push to `main` provisions billable resources and deploys the application.

The pipeline identity always needs Azure deployment/RBAC permissions. It only needs Microsoft Graph permissions when `EXISTING_ENTRA_CLIENT_ID` is empty. Azure Contributor/User Access Administrator assignments do not grant Microsoft Graph application permissions. To keep Graph rights out of CD after an initial privileged deployment, copy the authentication client ID into the repository Actions secret and the audience into the optional repository variable:

```powershell
$authenticationClientId = azd env get-value AZURE_ENTRA_CLIENT_ID
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($authenticationClientId)) {
    throw 'Provision or refresh the selected environment before reading its authentication client ID.'
}
$authenticationAudience = azd env get-value AZURE_ENTRA_API_AUDIENCE
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($authenticationAudience)) {
    throw 'Provision or refresh the selected environment before reading its authentication audience.'
}
gh secret set EXISTING_ENTRA_CLIENT_ID --body $authenticationClientId
gh variable set EXISTING_ENTRA_API_AUDIENCE --body $authenticationAudience
```

These values are public identifiers, but the client ID is stored as a secret to match the repository's deployment configuration. The workflow passes them to azd; it never uses `AZURE_CLIENT_ID` as the application's authentication ID. Redirects and consent must already be configured by an authorized administrator. See [requirements](requirements.md).

Updating azd to the available stable version is recommended, but do not rely on an upgrade alone to repair missing parameter mappings.

## Troubleshooting

### Pipeline configuration cannot find a managed identity passed to `--principal-id`

In azd 1.31.1, `--principal-id` triggers an application lookup even when the supplied client ID belongs to a valid managed identity. An error mentioning `could not find application` therefore does not mean the managed identity was deleted. Remove the option and use the managed-identity selection flow described above. Do not substitute the identity's object ID or resource ID into this option, or create an app registration just to bypass the error.

### Pipeline configuration reports a local prompt without a mapped variable

An error such as `parameter environmentName got its value from a local prompt ...` means that pipeline configuration cannot associate a resolved Bicep parameter with one environment variable. Setting `AZURE_ENV_NAME` alone is insufficient when the mapping file is missing.

Ensure `infra/main.parameters.json` exists and maps `environmentName` to `${AZURE_ENV_NAME}`, then rerun the configuration command for the selected environment. It also maps location, all three container images, and the capability-host flag, preventing the next required parameter from failing in the same way. Defaults match `main.bicepparam`, so old blank image prompt answers are not reused for pipeline configuration.

The command is not transactional: role assignments, federated credentials, and repository variables may already exist when the parameter merge fails. Inspect and reuse the configured identity instead of creating another one. There is no need to delete the local environment, recreate Azure resources, or switch to client secrets.

### `DEPLOYER_PRINCIPAL_ID` is empty

Resolve and set the signed-in user's object ID as shown in step 2. For CI, set the object ID of the automation identity.

### A MAI deployment is unavailable

Confirm the exact model version in the target region and subscription. Availability and quota vary independently. Change Bicep only after choosing a supported replacement and updating the architecture and requirements documentation.

### Capability-host connection is immutable

Run `azd provision` from the repository root so the preprovision hook executes. Inspect `DEPLOY_FOUNDRY_CAPABILITY_HOST` only if detection fails.

### Frontend authentication fails

Run `azd env refresh`, confirm the Entra outputs exist, and redeploy the `web` service. Do not commit generated production values.

When reusing an application, verify `EXISTING_ENTRA_CLIENT_ID`, its tenant, API identifier URI, the `access_as_user` scope/consent, and the SPA login-callback redirect for `SERVICE_WEB_URI`. Provisioning intentionally does not read or repair the registration in this mode.

### Provisioning fails with a Microsoft Graph permission error

If the deployment reports `Authorization_RequestDenied` or insufficient Graph privileges, either use an authorized principal for managed mode or supply `EXISTING_ENTRA_CLIENT_ID` with a preconfigured authentication application. In GitHub Actions, set the repository secret, not just the local azd environment. This only bypasses Graph application management; other failures, including Foundry capability-host errors, must be investigated independently.

### Private service access fails

Check Container Apps VNet integration, private endpoint state, private DNS links, and the managed identity's data-plane roles.

### Projects API returns HTTP 500

Confirm that the configured Cosmos database is `core` and contains the `projects`, `documents`, and `references` containers, each partitioned by `/ownerId`. Re-run `azd provision` after an infrastructure update if a required container is missing.

### SignalR live updates fail

Confirm that the API identity has both SignalR App Server and SignalR REST API Owner on the SignalR resource. The first role supports hub negotiation and server connections; the second permits the Dapr output binding to send owner-targeted events. Re-run `azd provision` after changing either assignment.

### Generation remains pending or fails repeatedly

Inspect the correct queue, job execution history, and Application Insights logs. Each worker takes one message with six hours of visibility and the job allows one concurrent execution, so a technical failure may wait for visibility expiry before a retry. The fifth dequeue deletes the failed message after logging it. The provisioned poison queues are not currently populated by the workers.

Check Cosmos status together with queue submission logs: persistence and enqueue are separate operations. A board can remain `Pending` if enqueue fails after persistence. Chapter prose/images are not queued. For a chapter image failure, correlate the API request with its dependency in Application Insights: the dependency path must be `/mai/v1/images/generations`; the API persists `ImageStatus=Failed` and returns sanitized `502` problem details without the prompt or provider response body.

## Remove an environment

Run only when you intend to delete the selected environment and its saved training content:

```powershell
azd down --purge
```

Review the selected environment and resources before confirming. Entra application cleanup and service-specific retention may require separate tenant or service-specific handling. Verify remaining billable resources afterwards. Individual comics and books can be permanently deleted from the authenticated project history.