# Deploy with Azure Developer CLI

This workflow provisions a billable educational environment solely for discovering Azure services and MAI models. It is not a production deployment recommendation. Read [requirements](requirements.md) and the [responsible AI FAQ](responsible-ai.md) first.

## 1. Authenticate

Azure Developer CLI manages the environment and deployment. Azure CLI is also required by the capability-host detection hook and to resolve the deployment principal.

```powershell
azd auth login
az login
az login --scope https://azuresre.dev/.default
```

Ensure both CLIs target the same tenant and subscription.

## 2. Create an environment

```powershell
azd env new
azd env set AZURE_LOCATION swedencentral
$principalId = az ad signed-in-user show --query id --output tsv
azd env set DEPLOYER_PRINCIPAL_ID $principalId
azd env set SRE_MONTHLY_AGENT_UNIT_LIMIT 500
# Optional: azd env set SRE_ALERT_EMAIL operations@example.invalid
```

Choose a short environment name because it contributes to Azure resource names. `AZURE_SUBSCRIPTION_ID` is selected by `azd env new` and stored only in the ignored `.azure` directory.

## 3. Validate prerequisites

Confirm the model names, versions, capacities, regional availability, and quota listed in [requirements](requirements.md). Confirm permission to create role assignments and Entra applications, then run:

```powershell
az bicep build --file .\infra\main.bicep --stdout > $null
azd show --output json
dotnet build .\src\MaiImageGenerator.slnx
```

Local compilation does not validate live permissions, model availability, quota, or policies. Review a preflight or what-if result in the intended subscription before provisioning; do not treat this sample as production-ready.

## 4. Provision and deploy

```powershell
azd up
```

The command performs these operations:

1. The `preprovision` hook checks for an existing Foundry capability host and sets `DEPLOY_FOUNDRY_CAPABILITY_HOST`.
2. Bicep creates the resources, private networking, identities, application registration, RBAC assignments, Azure Monitor alerts, and the read-only Azure SRE Agent.
3. The `postprovision` hook applies the SRE response plan, issue safeguards, and daily review through the agent data plane.
4. `azd` remotely builds and deploys the API, board-generation job, and book-plan-generation job containers.
5. The web `prepackage` hook writes `appsettings.Production.json` from Bicep outputs and publishes the Blazor application.
6. `azd` deploys the frontend to Static Web Apps.

The checked-in production appsettings is only a neutral template. Never replace it with tenant-specific values manually.

## 5. Verify

Use `azd env get-values` locally to inspect deployment outputs. Confirm `SERVICE_API_URI/healthz` returns HTTP 200, then open `SERVICE_WEB_URI` and sign in. Submit a small board and confirm that `generation-jobs` triggers only the board-generation job, panel notifications arrive, the job completes, and artifacts are downloadable. Then generate a book plan and confirm that `book-plan-jobs` triggers only the book-plan-generation job and that the page progresses from pending to completed. Verify that the browser acquires the API scope, the API accepts the token, both jobs use the managed identity, and telemetry reaches Application Insights.

Open Azure SRE Agent and confirm it is Running with Low access and Review mode, its three Azure connectors are connected, and the response plan and daily review exist. Complete the separately authorized, least-privilege GitHub connection and controlled alert test in [Azure SRE Agent operations](sre-agent.md); Bicep intentionally cannot authorize GitHub.

Avoid sharing the complete environment output in issues because it contains deployment-specific identifiers and endpoints.

### Functional smoke checks

| Check | Expected result |
| --- | --- |
| Create a comic and submit a template-matching board | A saved version appears; panels and final board become accessible through the API. |
| Close and reopen the board page | The background execution continues and saved progress can be retrieved. |
| View panel progress | SignalR targets the signed-in owner, and completion refreshes stored state/artifacts. |
| Create a book and request a short plan | API returns `202`; only the book-plan job consumes it; polling restores the completed plan. |
| Save chapter edits and generate prose, then an image | Separate stage states and one chapter image are saved; keep the page open for these foreground requests. |
| Reopen the book | Saved plan, prose, image, and stage errors survive navigation. |
| Use a second authorized test account | The first account's project/artifact IDs do not expose its data. |
| Request `/healthz` without a token | API returns `200`; Container Apps liveness/readiness and the availability test use the same endpoint. |
| Inspect Azure SRE Agent | Agent is Running in Review/Low mode; Azure Monitor, Application Insights, and Log Analytics connectors are connected. |

Use synthetic inputs and a small chapter/panel count to limit cost. Do not deliberately submit harmful material to exercise safety paths. These are manual smoke checks; the repository has no automated end-to-end test suite.

## Redeploy

Use `azd deploy` for application-only changes and `azd provision` when infrastructure changes. `azd up` performs both.

Foundry project connections become owned by the capability host after the first deployment. The preprovision hook detects an existing host and skips those immutable resources on subsequent provisions. Do not remove the hook without replacing this behavior.

The SRE postprovision hook uses idempotent PUT operations, so `azd provision` safely reapplies its response plan and daily task. Manual portal edits to those named objects are overwritten on the next provision.

## Rollback

For an application regression, check out the last known-good commit and run `azd deploy`. For infrastructure or SRE policy changes, check out the last known-good commit, review an Azure what-if result, and run `azd provision`. Do not roll back by manually deleting graph-owned resources.

To contain SRE automation immediately, disable the response plan and scheduled task, disconnect GitHub, and stop the agent. Revoke the GitHub App installation if repository access must end. Detailed rollback and disablement steps are in [Azure SRE Agent operations](sre-agent.md).

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

### Generation remains pending or fails repeatedly

Inspect the correct queue, job execution history, and Application Insights logs. Each worker takes one message with six hours of visibility and the job allows one concurrent execution, so a technical failure may wait for visibility expiry before a retry. The fifth dequeue deletes the failed message after logging it. The provisioned poison queues are not currently populated by the workers.

Check Cosmos status together with queue submission logs: persistence and enqueue are separate operations. A board can remain `Pending` if enqueue fails after persistence. For chapter prose/images, check the returned stage status and error even when the HTTP response is successful; those operations are not queued.

### SRE postprovision configuration fails

Run `az login --scope https://azuresre.dev/.default`, confirm Azure CLI and `azd` use the same tenant/subscription, and confirm the deployer has SRE Agent Administrator on the agent. Rerun `azd provision`; the hook is idempotent.

### SRE Agent does not create or update an issue

Code Access and the GitHub issue connector are separate. Verify both target only `mathieumack/foundry-comics-mai`, the GitHub App has Metadata read, Contents read, and Issues read/write, and create/update-issue is set to Ask. Inspect the agent thread before changing permissions or autonomy.

## Remove an environment

Run only when you intend to delete the selected environment and its saved training content:

```powershell
azd down --purge
```

Review the selected environment and resources before confirming. Entra application cleanup and soft-deleted resources may require separate tenant or service-specific handling. Verify remaining billable resources and any service retention behavior afterwards; the application has no per-project retention/deletion workflow.