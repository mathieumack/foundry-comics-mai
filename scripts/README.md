# Scripts

| Script | Purpose |
| --- | --- |
| `set-caphost-idempotency-flag.ps1` | Runs before provisioning and detects whether immutable Foundry capability-host resources already exist. |
| `prepare-frontend.ps1` | Builds the production Blazor configuration from `azd` outputs before packaging the web service. |
| `add-copyright-headers.ps1` | Maintainer utility for applying repository copyright headers. |

The first two scripts are invoked by `azure.yaml`; they are not independent deployment entry points. Follow [the deployment guide](../docs/deployment.md) for supported commands and troubleshooting.